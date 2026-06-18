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

        var sourceDir = BundleLayout.ResolveBundleDirectory(bundleDirectory);
        if (sourceDir is null)
            return BundlePublishResult.Fail($"No snapshot bundle (timeline.json) found under '{bundleDirectory}'.");

        // Gate 1: never publish a bundle we cannot prove is complete and intact. This re-hashes the source
        // against its own hashes.txt, including the reverse "no undeclared files" check.
        var sourceIntegrity = await SnapshotIntegrityVerifier.VerifyFolderAsync(sourceDir, cancellationToken);
        if (sourceIntegrity.Status != SnapshotIntegrityStatus.Verified)
            return BundlePublishResult.Fail(ResolveIssues(sourceIntegrity, "Source bundle"));

        var (collectionId, hostname) = await BundleLayout.ReadIdentityAsync(sourceDir, cancellationToken);
        if (string.IsNullOrWhiteSpace(collectionId))
            return BundlePublishResult.Fail(
                "manifest.json has no collectionId; cannot key the bundle in the repository.", hostname: hostname);

        var hostSegment = BundleLayout.SanitizeSegment(string.IsNullOrWhiteSpace(hostname) ? "unknown-host" : hostname!);
        var idSegment = BundleLayout.SanitizeSegment(collectionId!);
        var destFinal = Path.Combine(_repositoryRoot, hostSegment, idSegment);

        // Serialize everything that touches this destination's staging dir and final name, so two concurrent
        // publishes of the same collection (overlapping retries, intake server handling duplicate uploads)
        // cannot corrupt the shared ".partial" dir or race on the delete-then-rename finalize.
        using var _ = await KeyedAsyncLock.AcquireAsync(destFinal, cancellationToken);

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

    private static async Task<bool> FilesEqualAsync(string a, string b, CancellationToken cancellationToken)
    {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (!infoB.Exists || infoA.Length != infoB.Length)
            return false;

        var hashA = await BundleLayout.Sha256Async(a, cancellationToken);
        var hashB = await BundleLayout.Sha256Async(b, cancellationToken);
        return string.Equals(hashA, hashB, StringComparison.OrdinalIgnoreCase);
    }
}
