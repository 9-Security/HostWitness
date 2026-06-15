using System.Security.Cryptography;
using System.Text.Json;
using WinDFIR.Core.Snapshot;

namespace WinDFIR.Core.Repository;

/// <summary>
/// Publishes snapshot bundles into a filesystem case repository — a local directory, a UNC share, or a
/// mounted bucket. This is the zero-infrastructure deployment model for IR: every investigated host's
/// agent drops its verified bundle into one shared case folder, organized as
/// <c>&lt;repositoryRoot&gt;/&lt;hostname&gt;/&lt;collectionId&gt;/</c>.
///
/// Guarantees:
///  - <b>Integrity-gated</b>: the source bundle is re-verified against its own hashes.txt before anything is
///    copied, and the staged copy is verified again before it is exposed under its final name.
///  - <b>Idempotent</b>: if a verified bundle with the same collectionId is already present, the call is a no-op.
///  - <b>Resumable</b>: the copy lands in a sibling <c>.partial</c> directory; a retry re-copies only files that
///    are missing or differ, then atomically renames into place.
/// </summary>
public sealed class FileSystemArtifactSink : IArtifactSink
{
    private readonly string _repositoryRoot;

    public FileSystemArtifactSink(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
    }

    public string Describe() => $"filesystem case repository at '{_repositoryRoot}'";

    public async Task<BundlePublishResult> PublishBundleAsync(
        string bundleDirectory,
        IProgress<BundlePublishProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectory);

        var sourceDir = ResolveBundleDirectory(bundleDirectory);
        if (sourceDir is null)
            return BundlePublishResult.Fail($"No snapshot bundle (timeline.json) found under '{bundleDirectory}'.");

        // Gate 1: never publish a bundle we cannot prove is complete and intact. This re-hashes the source
        // against its own hashes.txt, including the reverse "no undeclared files" check.
        var sourceIntegrity = await SnapshotIntegrityVerifier.VerifyFolderAsync(sourceDir, cancellationToken);
        if (sourceIntegrity.Status != SnapshotIntegrityStatus.Verified)
            return BundlePublishResult.Fail(ResolveIssues(sourceIntegrity, "Source bundle"));

        var (collectionId, hostname) = await ReadBundleIdentityAsync(sourceDir, cancellationToken);
        if (string.IsNullOrWhiteSpace(collectionId))
            return BundlePublishResult.Fail(
                "manifest.json has no collectionId; cannot key the bundle in the repository.", hostname: hostname);

        var hostSegment = SanitizeSegment(string.IsNullOrWhiteSpace(hostname) ? "unknown-host" : hostname!);
        var idSegment = SanitizeSegment(collectionId!);
        var destFinal = Path.Combine(_repositoryRoot, hostSegment, idSegment);

        // Idempotent: a previously published, still-verifiable copy means there is nothing to do.
        if (Directory.Exists(destFinal))
        {
            var existing = await SnapshotIntegrityVerifier.VerifyFolderAsync(destFinal, cancellationToken);
            if (existing.Status == SnapshotIntegrityStatus.Verified)
            {
                return new BundlePublishResult
                {
                    Status = BundlePublishStatus.AlreadyPresent,
                    DestinationPath = destFinal,
                    CollectionId = collectionId,
                    Hostname = hostname
                };
            }
            // Present but not verifiable (a corrupt/partial earlier finalize): fall through and re-publish.
        }

        Directory.CreateDirectory(_repositoryRoot);
        var staging = destFinal + ".partial";
        Directory.CreateDirectory(staging);

        var sourceFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        var filesCopied = 0;
        var filesSkipped = 0;
        long bytesCopied = 0;

        for (var i = 0; i < sourceFiles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var src = sourceFiles[i];
            var relative = Path.GetRelativePath(sourceDir, src);
            var dest = Path.Combine(staging, relative);

            progress?.Report(new BundlePublishProgress
            {
                CurrentFile = relative,
                FilesProcessed = i,
                TotalFiles = sourceFiles.Length
            });

            // Resume: a file already staged with identical content from a prior interrupted run is left as-is.
            if (File.Exists(dest) && await FilesEqualAsync(src, dest, cancellationToken))
            {
                filesSkipped++;
                continue;
            }

            var destSubDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destSubDir))
                Directory.CreateDirectory(destSubDir);

            File.Copy(src, dest, overwrite: true);
            filesCopied++;
            bytesCopied += new FileInfo(src).Length;
        }

        // Gate 2: prove the staged copy is itself a complete, intact bundle before exposing it under the
        // final name. On failure we deliberately leave staging in place so a retry resumes, not restarts.
        var stagedIntegrity = await SnapshotIntegrityVerifier.VerifyFolderAsync(staging, cancellationToken);
        if (stagedIntegrity.Status != SnapshotIntegrityStatus.Verified)
            return BundlePublishResult.Fail(ResolveIssues(stagedIntegrity, "Staged copy"), collectionId, hostname);

        // Atomic finalize: staging and the final name share the repository volume, so Move is a rename.
        if (Directory.Exists(destFinal))
            Directory.Delete(destFinal, recursive: true);
        Directory.Move(staging, destFinal);

        return new BundlePublishResult
        {
            Status = BundlePublishStatus.Published,
            DestinationPath = destFinal,
            CollectionId = collectionId,
            Hostname = hostname,
            FilesCopied = filesCopied,
            FilesSkipped = filesSkipped,
            BytesCopied = bytesCopied
        };
    }

    private static IReadOnlyList<string> ResolveIssues(SnapshotIntegrityVerificationResult result, string subject) =>
        result.Issues.Count > 0
            ? result.Issues
            : new[] { $"{subject} integrity status: {result.Status}." };

    private static string? ResolveBundleDirectory(string path)
    {
        var dir = Path.GetFullPath(path);
        if (!Directory.Exists(dir))
            return null;

        if (File.Exists(Path.Combine(dir, "timeline.json")))
            return dir;

        try
        {
            foreach (var sub in Directory.GetDirectories(dir, "snapshot_*").OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(sub, "timeline.json")))
                    return sub;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static async Task<(string? collectionId, string? hostname)> ReadBundleIdentityAsync(
        string bundleDir, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(bundleDir, "manifest.json");
        if (!File.Exists(manifestPath))
            return (null, null);

        await using var stream = File.OpenRead(manifestPath);
        using var doc = await JsonDocument.ParseAsync(stream, default, cancellationToken);
        var root = doc.RootElement;

        var collectionId = root.TryGetProperty("collectionId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString()
            : null;

        string? hostname = null;
        if (root.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.Object
            && host.TryGetProperty("hostname", out var hn) && hn.ValueKind == JsonValueKind.String)
        {
            hostname = hn.GetString();
        }

        return (collectionId, hostname);
    }

    private static async Task<bool> FilesEqualAsync(string a, string b, CancellationToken cancellationToken)
    {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (!infoB.Exists || infoA.Length != infoB.Length)
            return false;

        var hashA = await Sha256Async(a, cancellationToken);
        var hashB = await Sha256Async(b, cancellationToken);
        return string.Equals(hashA, hashB, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> Sha256Async(string filePath, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Makes a manifest-derived value safe to use as a single path segment: invalid filename characters
    /// (including the path separators on Windows) become '_', and leading/trailing dots are stripped so a
    /// hostile or malformed value can never become "." / ".." or escape the repository root.
    /// </summary>
    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim().Trim('.').Trim();
        return string.IsNullOrEmpty(cleaned) ? "_" : cleaned;
    }
}
