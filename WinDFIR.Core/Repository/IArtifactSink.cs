namespace WinDFIR.Core.Repository;

/// <summary>
/// Destination for finished snapshot bundles. An <see cref="IArtifactSink"/> takes a completed,
/// hash-verified bundle directory (as produced by <see cref="WinDFIR.Core.Snapshot.SnapshotExporter"/>)
/// and publishes it into a central case repository so an IR analyst can gather collections from many
/// investigated hosts in one place.
///
/// Implementations must be:
///  - <b>idempotent</b>: publishing the same collection (same <c>collectionId</c>) twice is a no-op the
///    second time, never a duplicate or a corrupted overwrite;
///  - <b>resumable</b>: a publish interrupted partway can be retried and will complete without re-copying
///    bytes that already landed intact.
/// </summary>
public interface IArtifactSink
{
    /// <summary>Human-readable description of where this sink writes (for logs / status messages).</summary>
    string Describe();

    /// <summary>
    /// Publishes a finished bundle directory to the repository.
    /// </summary>
    /// <param name="bundleDirectory">
    /// Path to a completed bundle — either the bundle directory itself (containing timeline.json) or a
    /// parent directory holding a single <c>snapshot_*</c> child.
    /// </param>
    /// <param name="progress">Optional per-file progress reporting.</param>
    Task<BundlePublishResult> PublishBundleAsync(
        string bundleDirectory,
        IProgress<BundlePublishProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
