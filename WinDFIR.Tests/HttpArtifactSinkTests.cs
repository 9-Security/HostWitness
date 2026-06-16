using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Repository;
using WinDFIR.Core.Snapshot;
using Xunit;

namespace WinDFIR.Tests;

/// <summary>
/// Tests for the HTTP evidence-intake path (P5 multi-host): the <see cref="HttpArtifactSink"/> client and the
/// <see cref="BundleIntakeService"/> server. Client/server are exercised end-to-end over an in-process transport
/// (deterministic, no sockets), the server's safety logic is tested directly, and one smoke test runs the real
/// <see cref="HttpListenerBundleIntakeServer"/> over a loopback socket (self-skipping if it needs a URL ACL).
/// </summary>
public sealed class HttpArtifactSinkTests : IDisposable
{
    private readonly string _workDir;

    public HttpArtifactSinkTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "HostWitness_HttpSinkTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Publish_UploadsAndFinalizes_OverHttp()
    {
        var collectionId = Guid.NewGuid().ToString("D");
        var bundle = await CreateBundleAsync("c1", collectionId);
        var repoRoot = Path.Combine(_workDir, "repo");
        var service = new BundleIntakeService(repoRoot);
        using var http = NewInProcessClient(service);
        var sink = new HttpArtifactSink(http, "http://intake.test/");

        var result = await sink.PublishBundleAsync(bundle);

        Assert.Equal(BundlePublishStatus.Published, result.Status);
        Assert.Equal(collectionId, result.CollectionId);
        Assert.True(result.FilesCopied > 0);

        var dest = Path.Combine(repoRoot, Environment.MachineName, collectionId);
        var verify = await SnapshotIntegrityVerifier.VerifyFolderAsync(dest);
        Assert.Equal(SnapshotIntegrityStatus.Verified, verify.Status);
    }

    [Fact]
    public async Task Publish_IsIdempotent_OnSecondCall()
    {
        var collectionId = Guid.NewGuid().ToString("D");
        var bundle = await CreateBundleAsync("c1", collectionId);
        var repoRoot = Path.Combine(_workDir, "repo");
        var service = new BundleIntakeService(repoRoot);
        using var http = NewInProcessClient(service);
        var sink = new HttpArtifactSink(http, "http://intake.test/");

        var first = await sink.PublishBundleAsync(bundle);
        var second = await sink.PublishBundleAsync(bundle);

        Assert.Equal(BundlePublishStatus.Published, first.Status);
        Assert.Equal(BundlePublishStatus.AlreadyPresent, second.Status);
        Assert.Equal(0, second.FilesCopied);
    }

    [Fact]
    public async Task Publish_Resumes_WhenServerAlreadyHasSomeFiles()
    {
        var collectionId = Guid.NewGuid().ToString("D");
        var bundle = await CreateBundleAsync("c1", collectionId);
        var repoRoot = Path.Combine(_workDir, "repo");
        var service = new BundleIntakeService(repoRoot);

        // Pre-stage one identical file (manifest.json) as if a prior upload was interrupted after sending it.
        var manifestPath = Path.Combine(bundle, "manifest.json");
        var manifestSha = await Sha256Hex(manifestPath);
        await using (var fs = File.OpenRead(manifestPath))
        {
            var stored = await service.ReceiveFileAsync(collectionId, "manifest.json", manifestSha, fs);
            Assert.True(stored);
        }

        using var http = NewInProcessClient(service);
        var sink = new HttpArtifactSink(http, "http://intake.test/");
        var result = await sink.PublishBundleAsync(bundle);

        Assert.Equal(BundlePublishStatus.Published, result.Status);
        Assert.True(result.FilesSkipped >= 1, $"expected the pre-staged file to be skipped, got {result.FilesSkipped}");
    }

    [Fact]
    public async Task Publish_Fails_WhenSourceTampered_WithoutContactingServer()
    {
        var collectionId = Guid.NewGuid().ToString("D");
        var bundle = await CreateBundleAsync("c1", collectionId);
        var repoRoot = Path.Combine(_workDir, "repo");
        var service = new BundleIntakeService(repoRoot);
        using var http = NewInProcessClient(service);
        var sink = new HttpArtifactSink(http, "http://intake.test/");

        await File.AppendAllTextAsync(Path.Combine(bundle, "timeline.json"), "\n/* tampered */");

        var result = await sink.PublishBundleAsync(bundle);

        Assert.Equal(BundlePublishStatus.Failed, result.Status);
        Assert.False(Directory.Exists(Path.Combine(repoRoot, Environment.MachineName, collectionId)));
    }

    [Fact]
    public async Task ReceiveFile_RejectsPathTraversal_AndHashMismatch()
    {
        var repoRoot = Path.Combine(_workDir, "repo");
        var service = new BundleIntakeService(repoRoot);
        var bytes = Encoding.UTF8.GetBytes("payload");
        var goodSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        await using (var ms = new MemoryStream(bytes))
            Assert.False(await service.ReceiveFileAsync("id", @"..\..\evil.txt", goodSha, ms));

        await using (var ms = new MemoryStream(bytes))
            Assert.False(await service.ReceiveFileAsync("id", "ok.txt", "deadbeef", ms)); // wrong hash

        await using (var ms = new MemoryStream(bytes))
            Assert.True(await service.ReceiveFileAsync("id", "raw/ok.txt", goodSha, ms));
    }

    [Fact]
    public async Task Complete_Fails_WhenStagedSetIsIncomplete()
    {
        var collectionId = Guid.NewGuid().ToString("D");
        var bundle = await CreateBundleAsync("c1", collectionId);
        var repoRoot = Path.Combine(_workDir, "repo");
        var service = new BundleIntakeService(repoRoot);

        // Stage only the manifest (no timeline.json / hashes.txt) — the integrity gate must reject it.
        var manifestPath = Path.Combine(bundle, "manifest.json");
        await using (var fs = File.OpenRead(manifestPath))
            await service.ReceiveFileAsync(collectionId, "manifest.json", await Sha256Hex(manifestPath), fs);

        var result = await service.CompleteAsync(collectionId);

        Assert.Equal(nameof(BundlePublishStatus.Failed), result.Status);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public async Task Publish_OverRealLoopbackSocket()
    {
        var collectionId = Guid.NewGuid().ToString("D");
        var bundle = await CreateBundleAsync("c1", collectionId);
        var repoRoot = Path.Combine(_workDir, "repo");
        var service = new BundleIntakeService(repoRoot);

        var prefix = $"http://localhost:{GetFreePort()}/";
        HttpListenerBundleIntakeServer server;
        try
        {
            server = new HttpListenerBundleIntakeServer(service, prefix);
            server.Start();
        }
        catch (HttpListenerException)
        {
            return; // No URL ACL for this prefix on this machine (needs netsh/admin); skip the socket smoke test.
        }

        using (server)
        using (var http = new HttpClient())
        {
            var sink = new HttpArtifactSink(http, prefix);
            var result = await sink.PublishBundleAsync(bundle);

            Assert.Equal(BundlePublishStatus.Published, result.Status);
            var verify = await SnapshotIntegrityVerifier.VerifyFolderAsync(result.DestinationPath!);
            Assert.Equal(SnapshotIntegrityStatus.Verified, verify.Status);
        }
    }

    // --- helpers ---

    private static HttpClient NewInProcessClient(BundleIntakeService service) =>
        new(new InProcessIntakeHandler(service)) { BaseAddress = new Uri("http://intake.test/") };

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> Sha256Hex(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

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
            Summary = "http sink test",
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

    /// <summary>
    /// Routes HttpClient requests straight to a <see cref="BundleIntakeService"/> in-process, mirroring the
    /// <see cref="HttpListenerBundleIntakeServer"/> routing without opening a socket. Keeps the client tests
    /// deterministic and independent of URL-ACL / admin requirements.
    /// </summary>
    private sealed class InProcessIntakeHandler : HttpMessageHandler
    {
        private readonly BundleIntakeService _service;

        public InProcessIntakeHandler(BundleIntakeService service) => _service = service;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3 || segments[0] != "bundles")
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var id = Uri.UnescapeDataString(segments[1]);
            var action = segments[2].ToLowerInvariant();

            if (action == "status" && request.Method == HttpMethod.Get)
            {
                var status = await _service.GetStatusAsync(id, cancellationToken);
                return Json(status);
            }

            if (action == "files" && request.Method == HttpMethod.Put)
            {
                var rel = ParseQueryValue(request.RequestUri.Query, "path");
                request.Content!.Headers.TryGetValues(HttpIntakeContract.Sha256Header, out var shaValues);
                var sha = shaValues is null ? null : string.Join("", shaValues);
                if (string.IsNullOrEmpty(rel) || string.IsNullOrEmpty(sha))
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);

                await using var stream = await request.Content.ReadAsStreamAsync(cancellationToken);
                var ok = await _service.ReceiveFileAsync(id, rel, sha, stream, cancellationToken);
                return new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
            }

            if (action == "complete" && request.Method == HttpMethod.Post)
            {
                var result = await _service.CompleteAsync(id, cancellationToken);
                return Json(result);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string? ParseQueryValue(string query, string key)
        {
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0)
                    continue;
                if (Uri.UnescapeDataString(pair[..eq]) == key)
                    return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return null;
        }

        private static HttpResponseMessage Json<T>(T payload)
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload, HttpIntakeContract.Json);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/json") } }
            };
        }
    }
}
