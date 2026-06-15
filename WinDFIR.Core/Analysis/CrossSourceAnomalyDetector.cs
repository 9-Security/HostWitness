using System;
using System.Collections.Generic;
using System.Linq;

namespace WinDFIR.Core.Analysis;

/// <summary>Severity of a cross-source anomaly. All findings are advisory — a tripwire, not proof.</summary>
public enum AnomalyLevel
{
    Info = 0,
    Amber = 1,
    Red = 2
}

/// <summary>One item as seen from a single source (a live API path or a raw/offline path).</summary>
public sealed class CrossSourceItem
{
    /// <summary>Normalized identity used to match the same logical item across sources (e.g. lowercased service name).</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable label for display.</summary>
    public string Display { get; init; } = string.Empty;

    /// <summary>Optional comparable value (e.g. image path) used for value-mismatch detection.</summary>
    public string? Value { get; init; }
}

/// <summary>A discrepancy between what a live API reports and what the raw/offline source shows.</summary>
public sealed class CrossSourceAnomaly
{
    public required string Category { get; init; }     // e.g. "Service"
    public required string Kind { get; init; }         // MissingFromLive | MissingFromOffline | ValueMismatch
    public required AnomalyLevel Level { get; init; }
    public required string Key { get; init; }
    public string? Display { get; init; }
    public string? LiveValue { get; init; }
    public string? OfflineValue { get; init; }
    public required string Detail { get; init; }
}

/// <summary>
/// Compares two independent views of the same facts — one from a <b>live API</b> (which a kernel rootkit can
/// lie to) and one from a <b>raw/offline</b> source (the on-disk truth) — and reports the discrepancies. This
/// is the practical answer to a live tool's fundamental blind spot: you cannot guarantee you see a hidden
/// object, but a mismatch between "what the API says" and "what the raw artifact shows" is a high-value
/// tampering/hiding indicator. Pure and UI-independent so the rules are unit-testable.
/// </summary>
/// <remarks>
/// Findings are deliberately <see cref="AnomalyLevel.Amber"/> ("investigate"), not Red ("confirmed rootkit"):
/// benign causes exist (timing, disabled/just-deleted entries, source coverage differences), so the analyst
/// makes the final call. The two source sets MUST be filtered by the caller to the same class of item, or
/// coverage differences produce noise rather than signal.
/// </remarks>
public static class CrossSourceAnomalyDetector
{
    public const string MissingFromLive = "MissingFromLive";
    public const string MissingFromOffline = "MissingFromOffline";
    public const string ValueMismatch = "ValueMismatch";

    /// <param name="category">Logical category of the compared items (e.g. "Service").</param>
    /// <param name="liveItems">Items seen via the live API path.</param>
    /// <param name="offlineItems">Items seen via the raw/offline path.</param>
    /// <param name="compareValues">When true, same-key items with differing non-empty <see cref="CrossSourceItem.Value"/> are reported as a value mismatch.</param>
    public static List<CrossSourceAnomaly> Compare(
        string category,
        IEnumerable<CrossSourceItem> liveItems,
        IEnumerable<CrossSourceItem> offlineItems,
        bool compareValues = true)
    {
        var live = Index(liveItems);
        var offline = Index(offlineItems);
        var anomalies = new List<CrossSourceAnomaly>();

        // Present in the raw/offline source but NOT reported by the live API -> the classic "hidden by a hooked
        // API" indicator (also benignly: a disabled/recently-removed entry the live enumeration omits).
        foreach (var kv in offline)
        {
            if (!live.ContainsKey(kv.Key))
            {
                anomalies.Add(new CrossSourceAnomaly
                {
                    Category = category,
                    Kind = MissingFromLive,
                    Level = AnomalyLevel.Amber,
                    Key = kv.Key,
                    Display = kv.Value.Display,
                    OfflineValue = kv.Value.Value,
                    Detail = $"'{kv.Value.Display}' is present in the raw/offline {category.ToLowerInvariant()} source but was NOT reported by the live API. Possible API hiding/tampering — or a disabled/removed entry. Investigate."
                });
            }
        }

        // Present via the live API but NOT in the raw/offline source -> memory-only / injected, or created after
        // the offline source was captured.
        foreach (var kv in live)
        {
            if (!offline.ContainsKey(kv.Key))
            {
                anomalies.Add(new CrossSourceAnomaly
                {
                    Category = category,
                    Kind = MissingFromOffline,
                    Level = AnomalyLevel.Amber,
                    Key = kv.Key,
                    Display = kv.Value.Display,
                    LiveValue = kv.Value.Value,
                    Detail = $"'{kv.Value.Display}' is reported by the live API but is NOT in the raw/offline {category.ToLowerInvariant()} source. Possible memory-only/injected item — or created after capture. Investigate."
                });
            }
        }

        if (compareValues)
        {
            foreach (var kv in live)
            {
                if (offline.TryGetValue(kv.Key, out var off)
                    && !string.IsNullOrEmpty(kv.Value.Value)
                    && !string.IsNullOrEmpty(off.Value)
                    && !string.Equals(kv.Value.Value, off.Value, StringComparison.OrdinalIgnoreCase))
                {
                    anomalies.Add(new CrossSourceAnomaly
                    {
                        Category = category,
                        Kind = ValueMismatch,
                        Level = AnomalyLevel.Amber,
                        Key = kv.Key,
                        Display = kv.Value.Display,
                        LiveValue = kv.Value.Value,
                        OfflineValue = off.Value,
                        Detail = $"'{kv.Value.Display}' differs between live API and raw/offline ({category.ToLowerInvariant()}): live='{kv.Value.Value}' vs offline='{off.Value}'. Possible tampering. Investigate."
                    });
                }
            }
        }

        return anomalies;
    }

    private static Dictionary<string, CrossSourceItem> Index(IEnumerable<CrossSourceItem> items)
    {
        var map = new Dictionary<string, CrossSourceItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Key))
                continue;
            map[item.Key] = item; // last wins; dedups repeated keys
        }
        return map;
    }
}
