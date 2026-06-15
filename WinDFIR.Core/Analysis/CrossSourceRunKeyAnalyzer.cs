using System;
using System.Collections.Generic;
using System.Linq;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;

namespace WinDFIR.Core.Analysis;

/// <summary>
/// Applies <see cref="CrossSourceAnomalyDetector"/> to Run-key persistence: the live registry API view
/// (<c>RegistrySearchProvider</c>) versus the raw hive view (<c>OfflineHiveRegistryProvider</c>). A Run entry
/// in the raw hive but missing from the live API is a possible hiding/hooking indicator.
/// </summary>
/// <remarks>
/// Only the HKLM/HKCU <c>Run</c> scope is compared (the live provider covers Run, not RunOnce/Winlogon), keyed
/// on hive + value name. <b>No-op unless live registry collection is enabled</b> (it is off by default under the
/// forensic policy), so on a standard offline-only run there is nothing to compare against.
/// </remarks>
public static class CrossSourceRunKeyAnalyzer
{
    public const string Category = "RunKey";
    private const string LiveLabel = "live registry API";
    private const string OfflineLabel = "raw hive";

    public static List<ActivityEvent> Analyze(IActivityIndex index)
    {
        var registry = index.GetEventsByCategory("Registry").ToList();
        var live = BuildItems(registry, isOffline: false);
        var offline = BuildItems(registry, isOffline: true);

        if (live.Count == 0 || offline.Count == 0)
            return new List<ActivityEvent>();

        var anomalies = CrossSourceAnomalyDetector.Compare(
            Category, live, offline, compareValues: false,
            liveSourceName: LiveLabel, offlineSourceName: OfflineLabel);
        return anomalies.Select(ToEvent).ToList();
    }

    /// <summary>
    /// Builds Run-key items from registry events. <paramref name="isOffline"/> selects the raw-hive events
    /// (<c>Mode=Offline</c>) vs the live ones. Key = <c>hive|valueName</c>.
    /// </summary>
    public static List<CrossSourceItem> BuildItems(IEnumerable<ActivityEvent> registryEvents, bool isOffline)
    {
        var items = new Dictionary<string, CrossSourceItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in registryEvents)
        {
            var eventIsOffline = string.Equals(GetString(evt, "Mode"), "Offline", StringComparison.OrdinalIgnoreCase);
            if (eventIsOffline != isOffline)
                continue;

            var scope = MapRunScope(GetString(evt, "QueryName"));
            if (scope == null)
                continue;

            var valueName = GetString(evt, "ValueName");
            if (string.IsNullOrWhiteSpace(valueName))
                continue;

            var key = (scope + "|" + valueName.Trim()).ToLowerInvariant();
            items[key] = new CrossSourceItem
            {
                Key = key,
                Display = $"{scope}\\Run::{valueName.Trim()}",
                Value = OfflineValueDecoder.DecodeUtf16(GetString(evt, "ValueData"))
            };
        }
        return items.Values.ToList();
    }

    /// <summary>Maps a provider QueryName to the canonical Run hive (HKLM/HKCU), or null if not a comparable Run key.</summary>
    public static string? MapRunScope(string queryName)
    {
        return queryName switch
        {
            "System Run Key" => "HKLM",   // live HKLM Run
            "User Run Key" => "HKCU",     // live HKCU Run
            "Run" => "HKLM",              // offline SOFTWARE Run
            "User Run" => "HKCU",         // offline NTUSER Run
            _ => null
        };
    }

    private static ActivityEvent ToEvent(CrossSourceAnomaly anomaly)
    {
        var fields = new Dictionary<string, object>
        {
            ["AnomalyKind"] = anomaly.Kind,
            ["AnomalyCategory"] = anomaly.Category,
            ["Key"] = anomaly.Key,
            ["Analyzer"] = "CrossSource",
            ["Detail"] = anomaly.Detail
        };
        if (anomaly.Display != null) fields["Display"] = anomaly.Display;

        return new ActivityEvent
        {
            Category = "Anomaly",
            Action = "CrossSource",
            Timestamp = DateTime.UtcNow,
            Evidence = new List<EvidenceRef> { new EvidenceRef("CrossSourceAnalysis", $"{anomaly.Category}:{anomaly.Key}", null, DateTime.UtcNow) },
            Summary = $"[{anomaly.Kind}] {anomaly.Detail}",
            Fields = fields,
            Confidence = "Low"
        };
    }

    private static string GetString(ActivityEvent evt, string key)
    {
        if (evt.Fields != null && evt.Fields.TryGetValue(key, out var v) && v != null)
            return v.ToString() ?? string.Empty;
        return string.Empty;
    }
}
