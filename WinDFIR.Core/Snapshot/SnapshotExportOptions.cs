namespace WinDFIR.Core.Snapshot;

/// <summary>
/// Optional settings and metadata for snapshot export (e.g. known risks, ETW drop counts).
/// </summary>
public sealed class SnapshotExportOptions
{
    /// <summary>
    /// Extra key-value pairs to merge into manifest.json (e.g. "etwTotalDrops", "knownLimitations").
    /// Values must be JSON-serializable.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? ManifestExtras { get; init; }

    /// <summary>
    /// Source event count before export caps are applied. Optional, but enables manifest summaries to report truncation.
    /// </summary>
    public long? SourceEventCount { get; init; }

    /// <summary>
    /// Extra key-value pairs to merge into collectionSummary (e.g. preflight warning counts, UI drop totals).
    /// Values must be JSON-serializable.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? CollectionSummaryExtras { get; init; }
}