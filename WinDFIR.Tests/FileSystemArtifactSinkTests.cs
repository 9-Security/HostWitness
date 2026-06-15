using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Repository;
using WinDFIR.Core.Snapshot;
using Xunit;

namespace WinDFIR.Tests;

/// <summary>
/// Tests for publishing finished snapshot bundles into a central filesystem case repository
/// (P5 multi-host collection): integrity gating, idempotent re-publish, resume, and host/collection layout.
/// </summary>
public sealed class FileSystemArtifactSinkTests : IDisposable
{
    private readonly string _workDir;

    public FileSystemArtifactSinkTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "HostWitness_SinkTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Publish_CopiesVerifiedBundle_OrganizedByHostAndCollectionId()
    {
        var collectionId = Guid.NewGuid().ToString("D");
        var bundle = await CreateBundleAsync("c1", collectionId);
        var repoRoot = Path.Combine(_workDir, "repo");
        var sink = new FileSystemArtifactSink(repoRoot);

        var result = await sink.PublishBundleAsync(bundle);

        Assert.Equal(BundlePublishStatus.Published, result.Status);
        Assert.Equal(collectionId, result.CollectionId);
        Assert.True(result.FilesCopied > 0);
        Assert.NotNull(result.DestinationPath);

        // Layout: <repo>/<hostname>/<collectionId>/
        Assert.Equal(collectionId, Path.GetFileName(result.DestinationPath));
        Assert.Equal(repoRoot, Path.GetDirectoryName(Path.GetDirectoryName(result.DestinationPath)));

        // The published copy is itself a complete, verifiable bundle.
        var verify = await SnapshotIntegrityVerifier.VerifyFolderAsync(result.DestinationPath!);
        Assert.Equal(SnapshotIntegrityStatus.Verified, verify.Status);
    }

    [Fact]
    public async Task Publish_IsIdempotent_OnSecondCall()
    {
        var collectionId = Guid.NewGuid().ToString("D");
        var bundle = await CreateBundleAsync("c1", collectionId);
        var repoRoot = Path.Combine(_workDir, "repo");
        var sink = new FileSystemArtifactSink(repoRoot);

        var first = await sink.PublishBundleAsync(bundle);
        var second = await sink.PublishBundleAsync(bundle);

        Assert.Equal(BundlePublishStatus.Published, first.Status);
        Assert.Equal(BundlePublishStatus.AlreadyPresent, second.Status);
        Assert.Equal(0, second.FilesCopied);
        Assert.Equal(first.DestinationPath, second.DestinationPath);

        // Exactly one collection directory under the host, and no leftover .partial.
        var hostDir = Path.GetDirectoryName(first.DestinationPath)!;
        Assert.Single(Directory.GetDirectories(hostDir));
        Assert.Empty(Directory.GetDirectories(hostDir, "*.partial"));
    }

    [Fact]
    public async Task Publish_Resumes_WhenStagingAlreadyHasSomeFiles()
    {
        var collectionId = Guid.NewGuid().ToString("D");
        var bundle = await CreateBundleAsync("c1", collectionId);
        var repoRoot = Path.Combine(_workDir, "repo");

        // Simulate an interrupted earlier publish: a .partial staging dir already holding one identical file.
        var hostSegment = Environment.MachineName;
        var destFinal = Path.Combine(repoRoot, hostSegment, collectionId);
        var staging = destFinal + ".partial";
        Directory.CreateDirectory(staging);
        File.Copy(Path.Combine(bundle, "manifest.json"), Path.Combine(staging, "manifest.json"));

        var sink = new FileSystemArtifactSink(repoRoot);
        var result = await sink.PublishBundleAsync(bundle);

        Assert.Equal(BundlePublishStatus.Published, result.Status);
        // The pre-staged, content-identical manifest.json must be skipped, not re-copied.
        Assert.True(result.FilesSkipped >= 1, $"expected at least one skipped file, got {result.FilesSkipped}");
        Assert.False(Directory.Exists(staging));
        var verify = await SnapshotIntegrityVerifier.VerifyFolderAsync(result.DestinationPath!);
        Assert.Equal(SnapshotIntegrityStatus.Verified, verify.Status);
    }

    [Fact]
    public async Task Publish_Fails_WhenSourceBundleTampered()
    {
        var collectionId = Guid.NewGuid().ToString("D");
        var bundle = await CreateBundleAsync("c1", collectionId);
        var repoRoot = Path.Combine(_workDir, "repo");

        // Tamper: change timeline.json without updating hashes.txt -> source no longer verifies.
        await File.AppendAllTextAsync(Path.Combine(bundle, "timeline.json"), "\n/* tampered */");

        var sink = new FileSystemArtifactSink(repoRoot);
        var result = await sink.PublishBundleAsync(bundle);

        Assert.Equal(BundlePublishStatus.Failed, result.Status);
        Assert.NotEmpty(result.Issues);
        // Nothing should have been written to the repository.
        Assert.False(Directory.Exists(Path.Combine(repoRoot, Environment.MachineName, collectionId)));
    }

    [Fact]
    public async Task Export_WritesStableCollectionId_HonoringExtrasOverride()
    {
        var collectionId = Guid.NewGuid().ToString("D");
        var bundle = await CreateBundleAsync("c1", collectionId);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(bundle, "manifest.json")));
        Assert.True(doc.RootElement.TryGetProperty("collectionId", out var id));
        Assert.Equal(collectionId, id.GetString());
    }

    [Fact]
    public async Task Export_MintsCollectionId_WhenNotSupplied()
    {
        var index = new InMemoryActivityIndex(10);
        var outDir = Path.Combine(_workDir, "noid_out");
        Directory.CreateDirectory(outDir);
        var exporter = new SnapshotExporter { UseVssSnapshots = false };

        var bundle = await exporter.ExportAsync(index, outDir);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(bundle, "manifest.json")));
        Assert.True(doc.RootElement.TryGetProperty("collectionId", out var id));
        Assert.True(Guid.TryParse(id.GetString(), out _));
    }

    /// <summary>Exports a real bundle (with one raw artifact) and a deterministic collectionId.</summary>
    private async Task<string> CreateBundleAsync(string label, string collectionId)
    {
        var caseDir = Path.Combine(_workDir, label);
        var artifactDir = Path.Combine(caseDir, "artifacts");
        Directory.CreateDirectory(artifactDir);
        var artifactPath = Path.Combine(artifactDir, "recent.lnk");
        await File.WriteAllTextAsync(artifactPath, "artifact-bytes-" + label);

        var index = new InMemoryActivityIndex(100);
        index.AddEvent(new ActivityEvent
        {
            Timestamp = DateTime.UtcNow,
            Category = "File",
            Action = "Open",
            Summary = "sink test",
            Evidence = new List<EvidenceRef> { new("RecentLnk", artifactPath) }
        });

        var outDir = Path.Combine(caseDir, "out");
        Directory.CreateDirectory(outDir);
        var exporter = new SnapshotExporter { UseVssSnapshots = false };
        var options = new SnapshotExportOptions
        {
            ManifestExtras = new Dictionary<string, object?> { ["collectionId"] = collectionId }
        };
        return await exporter.ExportAsync(index, outDir, options);
    }
}
