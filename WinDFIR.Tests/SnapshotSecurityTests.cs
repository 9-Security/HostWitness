using System.Text.Json;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Snapshot;
using Xunit;

namespace WinDFIR.Tests;

public class SnapshotSecurityTests
{
    [Fact]
    public void LoadFromFolder_DoesNotResolveRawReferenceOutsideSnapshot()
    {
        var tempDir = CreateTempDirectory("HostWitness_SnapshotImportContainment_");
        try
        {
            var snapshotDir = Path.Combine(tempDir, "snapshot_demo");
            Directory.CreateDirectory(snapshotDir);
            Directory.CreateDirectory(Path.Combine(snapshotDir, "raw"));

            var outsidePath = Path.Combine(tempDir, "outside.txt");
            File.WriteAllText(outsidePath, "outside");
            File.WriteAllText(Path.Combine(snapshotDir, "timeline.json"), """
{
  "events": [
    {
      "timestamp": "2026-03-20T00:00:00Z",
      "category": "File",
      "action": "Open",
      "summary": "Traversal attempt",
      "evidence": [
        {
          "source": "RecentLnk",
          "reference": "raw/../../outside.txt"
        }
      ]
    }
  ]
}
""");

            var loaded = SnapshotImporter.LoadFromFolder(snapshotDir);
            var loadedEvent = loaded.GetEventsByTimeRange(DateTime.MinValue, DateTime.MaxValue).Single();
            Assert.Single(loadedEvent.Evidence);
            Assert.Equal("raw/../../outside.txt", loadedEvent.Evidence[0].Reference);
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    public async Task VerifyFolderAsync_ReturnsVerified_ForFreshExport()
    {
        var tempDir = CreateTempDirectory("HostWitness_SnapshotVerifyOk_");
        try
        {
            var snapshotDir = await ExportSnapshotAsync(tempDir);
            var result = await SnapshotIntegrityVerifier.VerifyFolderAsync(snapshotDir);

            Assert.Equal(SnapshotIntegrityStatus.Verified, result.Status);
            Assert.True(result.VerifiedFileCount >= 3);
            Assert.Empty(result.Issues);
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    public async Task VerifyFolderAsync_ReturnsFailed_WhenHashMismatchDetected()
    {
        var tempDir = CreateTempDirectory("HostWitness_SnapshotVerifyFail_");
        try
        {
            var snapshotDir = await ExportSnapshotAsync(tempDir);
            await File.AppendAllTextAsync(Path.Combine(snapshotDir, "timeline.json"), "\n# tampered");

            var result = await SnapshotIntegrityVerifier.VerifyFolderAsync(snapshotDir);

            Assert.Equal(SnapshotIntegrityStatus.Failed, result.Status);
            Assert.Contains(result.Issues, issue => issue.Contains("Hash mismatch", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    public async Task VerifyFolderAsync_ReturnsUnverified_WhenHashesMissing()
    {
        var tempDir = CreateTempDirectory("HostWitness_SnapshotVerifyMissingHashes_");
        try
        {
            var snapshotDir = await ExportSnapshotAsync(tempDir);
            File.Delete(Path.Combine(snapshotDir, "hashes.txt"));

            var result = await SnapshotIntegrityVerifier.VerifyFolderAsync(snapshotDir);

            Assert.Equal(SnapshotIntegrityStatus.Unverified, result.Status);
            Assert.Contains(result.Issues, issue => issue.Contains("hashes.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExportAsync_SkipsDisallowedArtifactSource()
    {
        var tempDir = CreateTempDirectory("HostWitness_SnapshotSkipSource_");
        try
        {
            var artifactPath = Path.Combine(tempDir, "note.txt");
            await File.WriteAllTextAsync(artifactPath, "demo");

            var index = new InMemoryActivityIndex(10);
            index.AddEvent(new ActivityEvent
            {
                Timestamp = DateTime.UtcNow,
                Category = "Process",
                Action = "Start",
                Summary = "Disallowed artifact source",
                Evidence = new List<EvidenceRef>
                {
                    new("LiveProcessProvider", artifactPath)
                }
            });

            var exporter = new SnapshotExporter { UseVssSnapshots = false };
            await exporter.ExportAsync(index, tempDir);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(GetSnapshotDirectory(tempDir), "manifest.json")));
            var summary = doc.RootElement.GetProperty("collectionSummary");
            Assert.Equal(1, summary.GetProperty("evidenceReferenceCount").GetInt32());
            Assert.Equal(0, summary.GetProperty("rewrittenEvidenceReferenceCount").GetInt32());
            Assert.Equal(0, summary.GetProperty("copiedArtifactFileCount").GetInt32());
            Assert.Equal(1, summary.GetProperty("skippedEvidenceReferenceCount").GetInt32());
            Assert.True(summary.GetProperty("wasArtifactCopyIncomplete").GetBoolean());
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExportAsync_CopiesBrowserHistoryReference_WithRecordSuffix()
    {
        var tempDir = CreateTempDirectory("HostWitness_SnapshotBrowserHistory_");
        try
        {
            var artifactPath = Path.Combine(tempDir, "History");
            await File.WriteAllTextAsync(artifactPath, "demo");

            var index = new InMemoryActivityIndex(10);
            index.AddEvent(new ActivityEvent
            {
                Timestamp = DateTime.UtcNow,
                Category = "Browser",
                Action = "Visit",
                Summary = "Browser history artifact",
                Evidence = new List<EvidenceRef>
                {
                    new("BrowserHistory", $"{artifactPath}:123")
                }
            });

            var exporter = new SnapshotExporter { UseVssSnapshots = false };
            await exporter.ExportAsync(index, tempDir);

            var snapshotDir = GetSnapshotDirectory(tempDir);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshotDir, "timeline.json")));
            var rewrittenReference = doc.RootElement
                .GetProperty("events")[0]
                .GetProperty("evidence")[0]
                .GetProperty("reference")
                .GetString();

            Assert.NotNull(rewrittenReference);
            Assert.StartsWith("raw/browser/", rewrittenReference!, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(snapshotDir, rewrittenReference!.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            CleanupDirectory(tempDir);
        }
    }

    private static async Task<string> ExportSnapshotAsync(string tempDir)
    {
        var index = new InMemoryActivityIndex(10);
        index.AddEvent(new ActivityEvent
        {
            Timestamp = DateTime.UtcNow,
            Category = "Process",
            Action = "Start",
            Summary = "Demo event",
            Evidence = new List<EvidenceRef>()
        });

        var exporter = new SnapshotExporter { UseVssSnapshots = false };
        await exporter.ExportAsync(index, tempDir);
        return GetSnapshotDirectory(tempDir);
    }

    private static string GetSnapshotDirectory(string tempDir)
    {
        return Directory.GetDirectories(tempDir)
            .Single(path => Path.GetFileName(path).StartsWith("snapshot_", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanupDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // ignore cleanup failures in tests
        }
    }
}
