using System.Net;
using System.Net.Http.Json;
using WinDFIR.Core.Snapshot;

namespace WinDFIR.Core.Repository;

/// <summary>
/// Publishes snapshot bundles to a central evidence-intake endpoint over HTTP — the networked counterpart to
/// <see cref="FileSystemArtifactSink"/>, for IR deployments where investigated hosts cannot reach a shared
/// file path but can reach an intake service.
///
/// Same guarantees as the filesystem sink, achieved over the wire:
///  - <b>Integrity-gated</b>: the source bundle is verified against its own hashes.txt before any upload; the
///    server re-verifies the assembled set before finalizing.
///  - <b>Idempotent</b>: a GET status that reports the collection already complete short-circuits to AlreadyPresent.
///  - <b>Resumable</b>: status reports which files are already staged; only missing/differing files are uploaded.
/// </summary>
public sealed class HttpArtifactSink : IArtifactSink
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    /// <param name="httpClient">Caller-owned client (lifetime, auth headers, and timeouts are the caller's concern).</param>
    /// <param name="baseUrl">Intake base URL, e.g. https://intake.example/.</param>
    public HttpArtifactSink(HttpClient httpClient, string baseUrl)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _baseUri = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
    }

    public string Describe() => $"HTTP evidence intake at '{_baseUri}'";

    public async Task<BundlePublishResult> PublishBundleAsync(
        string bundleDirectory,
        IProgress<BundlePublishProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectory);

        var sourceDir = BundleLayout.ResolveBundleDirectory(bundleDirectory);
        if (sourceDir is null)
            return BundlePublishResult.Fail($"No snapshot bundle (timeline.json) found under '{bundleDirectory}'.");

        // Gate: never upload a bundle we cannot prove is complete and intact.
        var sourceIntegrity = await SnapshotIntegrityVerifier.VerifyFolderAsync(sourceDir, cancellationToken);
        if (sourceIntegrity.Status != SnapshotIntegrityStatus.Verified)
        {
            return BundlePublishResult.Fail(
                sourceIntegrity.Issues.Count > 0
                    ? sourceIntegrity.Issues
                    : new[] { $"Source bundle integrity status: {sourceIntegrity.Status}." });
        }

        var (collectionId, hostname) = await BundleLayout.ReadIdentityAsync(sourceDir, cancellationToken);
        if (string.IsNullOrWhiteSpace(collectionId))
            return BundlePublishResult.Fail(
                "manifest.json has no collectionId; cannot key the bundle in the repository.", hostname: hostname);

        // 1. Status: short-circuit if already present, otherwise learn what is already staged (resume).
        var status = await GetStatusAsync(collectionId!, cancellationToken);
        if (status.Complete)
        {
            return new BundlePublishResult
            {
                Status = BundlePublishStatus.AlreadyPresent,
                DestinationPath = status.DestinationPath,
                CollectionId = collectionId,
                Hostname = hostname
            };
        }

        // 2. Upload every file the server does not already hold with a matching hash.
        var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        var filesCopied = 0;
        var filesSkipped = 0;
        long bytesCopied = 0;

        for (var i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var src = files[i];
            var relative = BundleLayout.RelativeKey(sourceDir, src);
            var sha = await BundleLayout.Sha256Async(src, cancellationToken);

            progress?.Report(new BundlePublishProgress
            {
                CurrentFile = relative,
                FilesProcessed = i,
                TotalFiles = files.Length
            });

            if (status.StagedFiles.TryGetValue(relative, out var stagedSha)
                && string.Equals(stagedSha, sha, StringComparison.OrdinalIgnoreCase))
            {
                filesSkipped++;
                continue;
            }

            await UploadFileAsync(collectionId!, relative, sha, src, cancellationToken);
            filesCopied++;
            bytesCopied += new FileInfo(src).Length;
        }

        // 3. Complete: the server verifies the assembled set and finalizes it into the repository.
        var complete = await CompleteAsync(collectionId!, cancellationToken);
        if (!Enum.TryParse<BundlePublishStatus>(complete.Status, out var finalStatus))
            finalStatus = BundlePublishStatus.Failed;

        if (finalStatus == BundlePublishStatus.Failed)
        {
            return BundlePublishResult.Fail(
                complete.Issues.Count > 0 ? complete.Issues : new[] { "Intake server reported publish failure." },
                collectionId, hostname);
        }

        return new BundlePublishResult
        {
            Status = finalStatus,
            DestinationPath = complete.DestinationPath,
            CollectionId = collectionId,
            Hostname = hostname,
            FilesCopied = filesCopied,
            FilesSkipped = filesSkipped,
            BytesCopied = bytesCopied
        };
    }

    private async Task<BundleStatusResponse> GetStatusAsync(string collectionId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            new Uri(_baseUri, HttpIntakeContract.StatusPath(collectionId)), cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return new BundleStatusResponse();

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BundleStatusResponse>(HttpIntakeContract.Json, cancellationToken)
               ?? new BundleStatusResponse();
    }

    private async Task UploadFileAsync(string collectionId, string relative, string sha, string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        using var content = new StreamContent(stream);
        content.Headers.Add(HttpIntakeContract.Sha256Header, sha);

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(_baseUri, HttpIntakeContract.FilesPath(collectionId, relative)))
        {
            Content = content
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Intake rejected file '{relative}' (HTTP {(int)response.StatusCode}).");
    }

    private async Task<BundleCompleteResponse> CompleteAsync(string collectionId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, HttpIntakeContract.CompletePath(collectionId)))
        {
            Content = new StringContent(string.Empty)
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BundleCompleteResponse>(HttpIntakeContract.Json, cancellationToken)
               ?? new BundleCompleteResponse { Issues = { "Intake returned an empty completion response." } };
    }
}
