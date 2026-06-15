using System;
using System.Collections.Generic;
using System.Linq;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;

namespace WinDFIR.Core.Analysis;

/// <summary>
/// Applies <see cref="CrossSourceAnomalyDetector"/> to scheduled tasks: the on-disk Task XML view
/// (<c>%WinDir%\System32\Tasks</c>) versus the registry <c>TaskCache</c> view. Windows keeps a task in both;
/// a desync — a task in TaskCache with no on-disk XML (hidden from the Task Scheduler UI / <c>schtasks</c>),
/// or vice versa — is a stealth-task indicator. Item extraction is split out for unit testing.
/// </summary>
public static class CrossSourceTaskAnalyzer
{
    public const string Category = "ScheduledTask";
    private const string OnDiskLabel = "on-disk Task XML";
    private const string TaskCacheLabel = "TaskCache registry";

    public static List<ActivityEvent> Analyze(IActivityIndex index)
    {
        var onDisk = BuildOnDiskItems(index.GetEventsByCategory("Persistence"));
        var taskCache = BuildTaskCacheItems(index.GetEventsByCategory("Registry"));

        if (onDisk.Count == 0 || taskCache.Count == 0)
            return new List<ActivityEvent>();

        var anomalies = CrossSourceAnomalyDetector.Compare(
            Category, onDisk, taskCache, compareValues: false,
            liveSourceName: OnDiskLabel, offlineSourceName: TaskCacheLabel);
        return anomalies.Select(ToEvent).ToList();
    }

    /// <summary>On-disk tasks: <c>Persistence</c>/<c>ScheduledTask</c> events from the Task-XML provider.</summary>
    public static List<CrossSourceItem> BuildOnDiskItems(IEnumerable<ActivityEvent> persistenceEvents)
    {
        // Filter by the presence of the TaskName field rather than Action: the index normalizes Action to a
        // canonical vocabulary (preserving the original in OriginalAction), so "ScheduledTask" is not reliable.
        // Only scheduled-task events carry TaskName (StartupFolder etc. use EntryName).
        var items = new List<CrossSourceItem>();
        foreach (var evt in persistenceEvents)
        {
            var name = GetString(evt, "TaskName");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            items.Add(new CrossSourceItem { Key = NormalizeTaskPath(name), Display = name.Trim() });
        }
        return items;
    }

    /// <summary>TaskCache tasks: offline-hive events carrying <c>TaskCache_Path</c> (the task tree path).</summary>
    public static List<CrossSourceItem> BuildTaskCacheItems(IEnumerable<ActivityEvent> registryEvents)
    {
        var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in registryEvents)
        {
            var raw = GetString(evt, "TaskCache_Path");
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var path = OfflineValueDecoder.DecodeUtf16(raw); // TaskCache_Path is hex-rendered by the offline provider
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var key = NormalizeTaskPath(path);
            items[key] = path.Trim();
        }
        return items.Select(kv => new CrossSourceItem { Key = kv.Key, Display = kv.Value }).ToList();
    }

    /// <summary>Normalizes a task path to a comparable key: lowercase, single leading backslash, no trailing slash.</summary>
    public static string NormalizeTaskPath(string path)
    {
        var p = path.Trim().Replace('/', '\\').ToLowerInvariant();
        p = p.TrimEnd('\\');
        if (!p.StartsWith("\\", StringComparison.Ordinal))
            p = "\\" + p;
        return p;
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
