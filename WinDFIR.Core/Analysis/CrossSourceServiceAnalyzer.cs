using System;
using System.Collections.Generic;
using System.Linq;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;

namespace WinDFIR.Core.Analysis;

/// <summary>
/// Applies <see cref="CrossSourceAnomalyDetector"/> to services: the live WMI <c>Win32_Service</c> view versus
/// the raw SYSTEM-hive Services view. A service present in the raw hive but missing from the live API is a
/// hiding/tampering indicator. Item extraction is split out so it is unit-testable without a populated index.
/// </summary>
public static class CrossSourceServiceAnalyzer
{
    public const string Category = "Service";

    /// <summary>
    /// Compares the two service views held in <paramref name="index"/> and returns anomaly events. Returns an
    /// empty list when either source is absent (cannot compare — never guesses).
    /// </summary>
    public static List<ActivityEvent> Analyze(IActivityIndex index)
    {
        var live = BuildLiveItems(index.GetEventsByCategory("Service"));
        var offline = BuildOfflineItems(index.GetEventsByCategory("Registry"));

        if (live.Count == 0 || offline.Count == 0)
            return new List<ActivityEvent>();

        // Presence-only comparison: image-path value comparison needs heavy normalization (quotes, args,
        // env-var expansion) that would create false mismatches, so it is deferred.
        var anomalies = CrossSourceAnomalyDetector.Compare(Category, live, offline, compareValues: false);
        return anomalies.Select(ToEvent).ToList();
    }

    /// <summary>Builds live-service items from <c>Category="Service"</c> events (WMI Win32_Service).</summary>
    public static List<CrossSourceItem> BuildLiveItems(IEnumerable<ActivityEvent> serviceEvents)
    {
        var items = new List<CrossSourceItem>();
        foreach (var evt in serviceEvents)
        {
            var name = GetString(evt, "ServiceName");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            items.Add(new CrossSourceItem
            {
                Key = name.Trim().ToLowerInvariant(),
                Display = name.Trim(),
                Value = GetString(evt, "ImagePath")
            });
        }
        return items;
    }

    /// <summary>
    /// Builds offline-service items from raw SYSTEM-hive Services events. The hive emits one event per value, so
    /// values are grouped by service name. Only clearly user-mode services (own/share/interactive process) are
    /// kept, because the live WMI <c>Win32_Service</c> source does not enumerate kernel drivers — comparing
    /// drivers would be coverage noise, not signal.
    /// </summary>
    public static List<CrossSourceItem> BuildOfflineItems(IEnumerable<ActivityEvent> registryEvents)
    {
        var byName = new Dictionary<string, ServiceAcc>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in registryEvents)
        {
            if (!string.Equals(GetString(evt, "QueryName"), "Services", StringComparison.OrdinalIgnoreCase))
                continue;
            var name = GetString(evt, "ServiceName");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var key = name.Trim();
            if (!byName.TryGetValue(key, out var acc))
                acc = new ServiceAcc { Display = key };

            // The offline hive provider renders value data as hex byte strings (UTF-16 for REG_SZ/EXPAND_SZ,
            // little-endian for DWORD), so decode before use.
            var imagePath = DecodeUtf16(GetString(evt, "ServiceImagePath"));
            if (!string.IsNullOrWhiteSpace(imagePath))
                acc.ImagePath = imagePath;

            var label = GetString(evt, "ServiceTypeLabel");
            if (label.Contains("Driver", StringComparison.OrdinalIgnoreCase))
                acc.DriverLabel = true;
            else if (label.Contains("Process", StringComparison.OrdinalIgnoreCase))
                acc.ProcessLabel = true;

            var type = DecodeDword(GetString(evt, "ServiceType"));
            if (type.HasValue)
                acc.ServiceType = type;

            byName[key] = acc;
        }

        return byName
            .Where(kv => IsUserModeService(kv.Value)) // only services WMI Win32_Service would also enumerate
            .Select(kv => new CrossSourceItem
            {
                Key = kv.Key.ToLowerInvariant(),
                Display = kv.Value.Display,
                Value = kv.Value.ImagePath
            })
            .ToList();
    }

    private sealed class ServiceAcc
    {
        public string Display = string.Empty;
        public string? ImagePath;
        public bool ProcessLabel;
        public bool DriverLabel;
        public int? ServiceType;
    }

    /// <summary>Decodes a space-separated hex byte string as UTF-16LE text; returns the input unchanged if it is not hex.</summary>
    private static string DecodeUtf16(string raw)
    {
        var bytes = TryParseHexBytes(raw);
        if (bytes == null)
            return raw;
        var len = bytes.Length - (bytes.Length % 2);
        return len <= 0 ? string.Empty : System.Text.Encoding.Unicode.GetString(bytes, 0, len).TrimEnd('\0');
    }

    /// <summary>Decodes a service Type value, accepting a decimal string or a hex-byte (LE DWORD) rendering.</summary>
    private static int? DecodeDword(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (int.TryParse(raw, out var d))
            return d;
        var bytes = TryParseHexBytes(raw);
        if (bytes != null && bytes.Length >= 4)
            return BitConverter.ToInt32(bytes, 0);
        return null;
    }

    private static byte[]? TryParseHexBytes(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        var trimmed = s.Replace("...", string.Empty).Trim();
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length != 2 || !byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
                return null; // not a hex byte string -> already readable text
        }
        return bytes;
    }

    /// <summary>
    /// Decides whether a hive service is a user-mode (Win32) service — the class WMI Win32_Service also
    /// enumerates. Classification is driven primarily by the image path (drivers are <c>.sys</c> under
    /// <c>\drivers\</c>; user-mode services are <c>.exe</c>), which is robust to how the Type DWORD is rendered.
    /// </summary>
    private static bool IsUserModeService(ServiceAcc acc)
    {
        if (acc.DriverLabel)
            return false;
        if (acc.ProcessLabel)
            return true;

        // Type DWORD bits: own=0x10, share=0x20 (user-mode); kernel=0x01, fs=0x02, recognizer=0x08 (driver).
        if (acc.ServiceType.HasValue)
            return (acc.ServiceType.Value & 0x30) != 0;

        // Fallback: classify by the (decoded) image path — drivers are .sys under \drivers\.
        if (string.IsNullOrWhiteSpace(acc.ImagePath))
            return false;

        var path = acc.ImagePath!.Trim().Trim('"');
        if (path.Contains(@"\drivers\", StringComparison.OrdinalIgnoreCase))
            return false;
        var firstToken = path;
        var exeIdx = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx >= 0)
            firstToken = path[..(exeIdx + 4)];
        if (firstToken.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))
            return false;
        return firstToken.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static ActivityEvent ToEvent(CrossSourceAnomaly anomaly)
    {
        var fields = new Dictionary<string, object>
        {
            ["AnomalyKind"] = anomaly.Kind,
            ["AnomalyCategory"] = anomaly.Category,
            ["Key"] = anomaly.Key,
            ["Analyzer"] = "CrossSource"
        };
        if (anomaly.Display != null) fields["Display"] = anomaly.Display;
        if (anomaly.LiveValue != null) fields["LiveValue"] = anomaly.LiveValue;
        if (anomaly.OfflineValue != null) fields["OfflineValue"] = anomaly.OfflineValue;
        fields["Detail"] = anomaly.Detail;

        return new ActivityEvent
        {
            Category = "Anomaly",
            Action = "CrossSource",
            Timestamp = DateTime.UtcNow,
            Evidence = new List<EvidenceRef> { new EvidenceRef("CrossSourceAnalysis", $"{anomaly.Category}:{anomaly.Key}", null, DateTime.UtcNow) },
            Summary = $"[{anomaly.Kind}] {anomaly.Detail}",
            Fields = fields,
            Confidence = "Low" // advisory tripwire — benign causes exist; analyst confirms
        };
    }

    private static string GetString(ActivityEvent evt, string key)
    {
        if (evt.Fields != null && evt.Fields.TryGetValue(key, out var v) && v != null)
            return v.ToString() ?? string.Empty;
        return string.Empty;
    }
}
