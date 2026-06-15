namespace WinDFIR.Core.Index;

/// <summary>Traffic-light level for live-session collection completeness.</summary>
public enum CollectionCompletenessLevel
{
    Green = 0,
    Amber = 1,
    Red = 2
}

/// <summary>
/// A red/amber/green view of whether the live session is currently losing events — the live-session
/// analogue of the snapshot Collection Trust dashboard. Surfaces in-memory index eviction (permanent
/// loss) and UI render drops (cosmetic) so an analyst is not misled into thinking the timeline is complete.
/// </summary>
public sealed class LiveCollectionStatus
{
    public required CollectionCompletenessLevel Level { get; init; }

    /// <summary>True when the warning should be shown (Level is not Green).</summary>
    public bool ShouldWarn => Level != CollectionCompletenessLevel.Green;

    /// <summary>True when events are being permanently lost from the in-memory index (will be absent from exports).</summary>
    public bool IsLosingData { get; init; }

    /// <summary>Short status line for the status bar.</summary>
    public required string Headline { get; init; }

    /// <summary>Longer explanation + remediation, suitable for a tooltip.</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// Pure, UI-independent rules for live-session completeness. Kept in Core so the thresholds and wording
/// are unit-testable (mirrors <c>CollectionTrustAssessor</c> for snapshots).
/// </summary>
public static class LiveCollectionAssessor
{
    /// <summary>Fraction of capacity at which we warn that eviction is imminent.</summary>
    public const double NearCapacityFraction = 0.9;

    /// <summary>
    /// Assesses live completeness from the index counters and UI render-drop total.
    /// </summary>
    /// <param name="indexEventCount">Current live event count held in the in-memory index.</param>
    /// <param name="indexMaxCapacity">In-memory index cap (0 = unbounded).</param>
    /// <param name="evictedEvents">Events permanently evicted from the index because the cap was exceeded.</param>
    /// <param name="uiRenderDropped">Events dropped from the live UI render queue (does NOT affect the persisted index).</param>
    public static LiveCollectionStatus Assess(long indexEventCount, int indexMaxCapacity, long evictedEvents, long uiRenderDropped)
    {
        // Worst case first: the in-memory index has overflowed and is discarding the oldest events.
        // These are gone from the index and therefore from any export taken now — this is real, permanent loss.
        if (evictedEvents > 0)
        {
            return new LiveCollectionStatus
            {
                Level = CollectionCompletenessLevel.Red,
                IsLosingData = true,
                Headline = $"⚠ Index full — {evictedEvents:N0} oldest event(s) dropped from memory",
                Detail =
                    $"The in-memory index hit its {indexMaxCapacity:N0}-event cap and has permanently discarded " +
                    $"{evictedEvents:N0} of the oldest event(s). They are NO LONGER in the timeline and will NOT be " +
                    "included in an export. Raise \"Max events\" in Settings (or set 0 for unbounded), or export now " +
                    "before more are lost. What you see is not the full history."
            };
        }

        // Approaching the cap: eviction has not started yet, but is imminent.
        if (indexMaxCapacity > 0 && indexEventCount >= (long)(NearCapacityFraction * indexMaxCapacity))
        {
            var pct = (int)(100.0 * indexEventCount / indexMaxCapacity);
            return new LiveCollectionStatus
            {
                Level = CollectionCompletenessLevel.Amber,
                IsLosingData = false,
                Headline = $"Index ~{pct}% full — eviction imminent",
                Detail =
                    $"The in-memory index is ~{pct}% of its {indexMaxCapacity:N0}-event cap. Once full, the oldest " +
                    "events will be dropped. Raise \"Max events\" in Settings or export now to avoid losing history."
            };
        }

        // Render-queue drops only: the persisted index is intact, but the live view skipped some events for
        // responsiveness during bursts. Worth flagging so "the live view looked sparse" is not mistaken for truth.
        if (uiRenderDropped > 0)
        {
            return new LiveCollectionStatus
            {
                Level = CollectionCompletenessLevel.Amber,
                IsLosingData = false,
                Headline = $"Live view dropped {uiRenderDropped:N0} render event(s)",
                Detail =
                    $"{uiRenderDropped:N0} event(s) were skipped from the live render during high-rate bursts to keep " +
                    "the UI responsive. The persisted index and exports are UNAFFECTED — reopen/refresh or export to " +
                    "see the complete set."
            };
        }

        return new LiveCollectionStatus
        {
            Level = CollectionCompletenessLevel.Green,
            IsLosingData = false,
            Headline = "Collection complete",
            Detail = "No events have been evicted from the index or dropped from the live view."
        };
    }
}
