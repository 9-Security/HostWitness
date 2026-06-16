using WinDFIR.Core.Snapshot;

namespace WinDFIR.Core.Repository;

/// <summary>
/// Server-side evidence intake: receives a snapshot bundle's files over any transport into a per-collection
/// staging area, then finalizes the completed set into the case repository. The finalize reuses
/// <see cref="FileSystemArtifactSink"/>, so the same integrity gate (verify against hashes.txt), idempotency
/// (verified destination = no-op), and atomic rename apply identically to HTTP-delivered and file-copied bundles.
///
/// This class is transport-agnostic; <see cref="HttpListenerBundleIntakeServer"/> is a thin HTTP front for it.
/// </summary>
public sealed class BundleIntakeService
{
    private readonly string _repositoryRoot;
    private readonly string _intakeWork;
    private readonly FileSystemArtifactSink _sink;

    public BundleIntakeService(string repositoryRoot, string? intakeWorkDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        // Staging defaults to a dot-prefixed subdir so it is skipped when scanning the repo for published hosts.
        _intakeWork = Path.GetFullPath(intakeWorkDirectory ?? Path.Combine(_repositoryRoot, ".intake"));
        _sink = new FileSystemArtifactSink(_repositoryRoot);
    }

    public async Task<BundleStatusResponse> GetStatusAsync(string collectionId, CancellationToken cancellationToken = default)
    {
        var id = BundleLayout.SanitizeSegment(collectionId);

        var published = FindPublished(id);
        if (published is not null)
        {
            var verify = await SnapshotIntegrityVerifier.VerifyFolderAsync(published, cancellationToken);
            if (verify.Status == SnapshotIntegrityStatus.Verified)
                return new BundleStatusResponse { Complete = true, DestinationPath = published };
        }

        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stagingDir = StagingDir(id);
        if (Directory.Exists(stagingDir))
        {
            foreach (var file in Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                staged[BundleLayout.RelativeKey(stagingDir, file)] = await BundleLayout.Sha256Async(file, cancellationToken);
            }
        }

        return new BundleStatusResponse { Complete = false, StagedFiles = staged };
    }

    /// <summary>
    /// Stores one uploaded file into staging. Returns false (and stores nothing) if the relative path would
    /// escape the staging directory, or if the received bytes do not match the declared sha256.
    /// </summary>
    public async Task<bool> ReceiveFileAsync(
        string collectionId, string relativePath, string expectedSha256, Stream content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(expectedSha256))
            return false;

        var id = BundleLayout.SanitizeSegment(collectionId);
        var stagingDir = StagingDir(id);
        var dest = Path.GetFullPath(Path.Combine(stagingDir, relativePath));

        // Path-traversal guard: a hostile relativePath (e.g. ..\..\evil) must never write outside staging.
        if (!BundleLayout.IsWithinDirectory(dest, stagingDir))
            return false;

        // Receive into a uniquely-named temp file in a separate scratch dir (never inside the bundle staging
        // dir), verify the hash, then atomically move it into place. Because temps live outside staging, an
        // interrupted, cancelled, or hash-mismatched upload can never leave an orphan that the integrity gate's
        // "undeclared file" check would later reject — only verified, complete files ever land in staging.
        var scratchDir = Path.Combine(_intakeWork, ".tmp");
        Directory.CreateDirectory(scratchDir);
        var temp = Path.Combine(scratchDir, Guid.NewGuid().ToString("N") + ".part");

        try
        {
            await using (var fs = File.Create(temp))
            {
                await content.CopyToAsync(fs, cancellationToken);
            }

            var actual = await BundleLayout.Sha256Async(temp, cancellationToken);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(temp, dest, overwrite: true);
            return true;
        }
        finally
        {
            TryDeleteFile(temp); // no-op once moved into place; cleans up on mismatch/cancellation/error
        }
    }

    /// <summary>Finalizes the staged set into the repository (integrity-gated) and clears staging on success.</summary>
    public async Task<BundleCompleteResponse> CompleteAsync(string collectionId, CancellationToken cancellationToken = default)
    {
        var id = BundleLayout.SanitizeSegment(collectionId);
        var stagingDir = StagingDir(id);

        if (!Directory.Exists(stagingDir))
            return new BundleCompleteResponse { Issues = { "No staged files were received for this collection." } };

        var result = await _sink.PublishBundleAsync(stagingDir, null, cancellationToken);
        var response = new BundleCompleteResponse
        {
            Status = result.Status.ToString(),
            DestinationPath = result.DestinationPath,
            CollectionId = result.CollectionId,
            Hostname = result.Hostname,
            Issues = result.Issues.ToList()
        };

        if (result.IsSuccess)
            TryDeleteDirectory(stagingDir);

        return response;
    }

    private string StagingDir(string sanitizedId) => Path.Combine(_intakeWork, sanitizedId);

    private string? FindPublished(string sanitizedId)
    {
        if (!Directory.Exists(_repositoryRoot))
            return null;

        // Repository layout is <repo>/<host>/<collectionId>. Search across hosts for this collectionId,
        // skipping dot-prefixed control dirs (e.g. the .intake staging area).
        foreach (var hostDir in Directory.GetDirectories(_repositoryRoot))
        {
            if (Path.GetFileName(hostDir).StartsWith('.'))
                continue;

            var candidate = Path.Combine(hostDir, sanitizedId);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
