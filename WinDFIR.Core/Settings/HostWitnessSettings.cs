using System.Text.Json;

namespace WinDFIR.Core.Settings;

public sealed class HostWitnessSettings
{
    public SettingsHelp Help { get; set; } = SettingsHelp.CreateDefault();
    public UiSettings Ui { get; set; } = new();
    public ProcessCacheSettings ProcessCache { get; set; } = new();
    public IndexSettings Index { get; set; } = new();

    public static string SettingsPath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "HostWitness", "settings.json");
        }
    }

    public static HostWitnessSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
                return new HostWitnessSettings();

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<HostWitnessSettings>(json, JsonOptions);
            var normalized = settings ?? new HostWitnessSettings();
            if (Normalize(normalized))
                Save(normalized);
            return normalized;
        }
        catch
        {
            return new HostWitnessSettings();
        }
    }

    public static void EnsureSettingsFile()
    {
        try
        {
            var path = SettingsPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            if (File.Exists(path))
            {
                var settings = Load();
                Save(settings);
                return;
            }

            var defaults = new HostWitnessSettings();
            var json = JsonSerializer.Serialize(defaults, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Ignore settings persistence failures
        }
    }

    public static bool Save(HostWitnessSettings settings)
    {
        try
        {
            var path = SettingsPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(path, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int GetEffectiveFontSize(HostWitnessSettings settings)
    {
        var mode = settings.Ui.FontSizeMode?.Trim().ToLowerInvariant();
        return mode switch
        {
            "small" => 12,
            "large" => 20,
            "custom" => NormalizeFontSize(settings.Ui.CustomFontSize, 16),
            _ => 16
        };
    }

    private static int NormalizeFontSize(int value, int fallback)
    {
        if (value <= 0)
            return fallback;

        if (value < 8)
            return 8;

        if (value > 72)
            return 72;

        return value;
    }

    private static bool Normalize(HostWitnessSettings settings)
    {
        var changed = false;

        if (settings.Help == null)
        {
            settings.Help = SettingsHelp.CreateDefault();
            changed = true;
        }
        else
        {
            settings.Help.ProcessCache ??= new SettingsHelpProcessCache();
            settings.Help.Ui ??= new SettingsHelpUi();
            settings.Help.Index ??= new SettingsHelpIndex();
            settings.Help.Notes ??= new List<string>();
        }

        if (settings.Ui == null)
        {
            settings.Ui = new UiSettings();
            changed = true;
        }

        if (settings.ProcessCache == null)
        {
            settings.ProcessCache = new ProcessCacheSettings();
            changed = true;
        }

        if (settings.Index == null)
        {
            settings.Index = new IndexSettings();
            changed = true;
        }

        if (settings.ProcessCache.EventLog == null)
        {
            settings.ProcessCache.EventLog = new CachePolicy();
            changed = true;
        }

        if (settings.ProcessCache.Etw == null)
        {
            settings.ProcessCache.Etw = new CachePolicy();
            changed = true;
        }

        if (settings.ProcessCache.EtwThrottleMaxPerSecond < 0)
        {
            settings.ProcessCache.EtwThrottleMaxPerSecond = 0;
            changed = true;
        }
        else if (TryUpgradeLegacyForensicStrictDefaults(settings))
        {
            changed = true;
        }

        settings.ProcessCache.LongLivedProcessNames ??= new List<string>();

        if (settings.Index.MaxEvents < 0)
            settings.Index.MaxEvents = 0;

        if (string.IsNullOrWhiteSpace(settings.Ui.FontSizeMode))
            settings.Ui.FontSizeMode = "Medium";

        if (string.IsNullOrWhiteSpace(settings.Ui.TimeZoneDisplay))
            settings.Ui.TimeZoneDisplay = "Local";

        settings.Ui.RawHiveSources ??= new List<RawHiveSourceItem>();

        return changed;
    }

    private static bool TryUpgradeLegacyForensicStrictDefaults(HostWitnessSettings settings)
    {
        if (settings.Ui.RegistryUseOfflineOnly != true || settings.Ui.EnableLiveRegistryExperimental != false)
            return false;
        if (settings.ProcessCache.EtwThrottleMaxPerSecond != 0)
            return false;
        if (settings.Index.MaxEvents != 200_000 || settings.ProcessCache.LongLivedTtlMinutes != 10_080)
            return false;
        if (!MatchesCachePolicy(settings.ProcessCache.EventLog, 720, 1440, 20_000))
            return false;
        if (!MatchesCachePolicy(settings.ProcessCache.Etw, 30, 120, 50_000))
            return false;

        settings.ProcessCache.EtwThrottleMaxPerSecond = 300;
        return true;
    }

    private static bool MatchesCachePolicy(CachePolicy? policy, int provisionalTtlMinutes, int authoritativeTtlMinutes, int maxEntries)
    {
        return policy != null
            && policy.ProvisionalTtlMinutes == provisionalTtlMinutes
            && policy.AuthoritativeTtlMinutes == authoritativeTtlMinutes
            && policy.MaxEntries == maxEntries;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

public sealed class SettingsHelp
{
    public SettingsHelpProcessCache ProcessCache { get; set; } = new();
    public SettingsHelpUi Ui { get; set; } = new();
    public SettingsHelpIndex Index { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public static SettingsHelp CreateDefault()
    {
        return new SettingsHelp
        {
            ProcessCache = new SettingsHelpProcessCache
            {
                EventLog = "EventLog PID cache settings (TTL minutes + LRU max). Provisional=eventTimestamp fallback; Authoritative=parsed CreateTime.",
                Etw = "ETW PID cache settings (TTL minutes + LRU max). Provisional=eventTimestamp fallback; Authoritative=parsed CreateTime.",
                EtwThrottleMaxPerSecond = "ETW throttle cap: max events per second per category. Default stable value is 300. Set 0 to use built-in defaults (File 500, Registry/Network 300). Raising may reduce drops but increase CPU and memory; restart to apply.",
                LongLivedTtlMinutes = "TTL for long-lived processes (minutes). Applies when ProcessName matches LongLivedProcessNames.",
                LongLivedProcessNames = "List of long-lived process names (case-insensitive). Use file name only, e.g. svchost.exe."
            },
            Ui = new SettingsHelpUi
            {
                FontSizeMode = "Global UI font size: Small (12), Medium (16), Large (20), or Custom.",
                CustomFontSize = "Custom font size (8-72). Used only when mode is Custom.",
                TimeZoneDisplay = "Timestamp display: Local or UTC (Timeline and other views).",
                RegistryUseOfflineOnly = "Forensic-safe default. When enabled, do not use Live Registry APIs.",
                EnableLiveRegistryExperimental = "Enables Live Registry collection/search (non-forensic, API-hookable). Use only for triage.",
                ShowStatusBarDiagnostics = "Show status bar diagnostics (PID cache, ETW throttle, UI queue backpressure). Restart to apply."
            },
            Index = new SettingsHelpIndex
            {
                MaxEvents = "Activity index max events; oldest are evicted. 0 = unbounded (use with caution)."
            },
            Notes = new List<string>
            {
                "Changing settings requires restarting HostWitness.",
                "MaxEntries is an LRU upper bound; oldest LastSeen entries are evicted first.",
                "Set MaxEntries to 0 to disable size limit."
            }
        };
    }
}

public sealed class SettingsHelpProcessCache
{
    public string EventLog { get; set; } = string.Empty;
    public string Etw { get; set; } = string.Empty;
    public string EtwThrottleMaxPerSecond { get; set; } = string.Empty;
    public string LongLivedTtlMinutes { get; set; } = string.Empty;
    public string LongLivedProcessNames { get; set; } = string.Empty;
}

public sealed class SettingsHelpUi
{
    public string FontSizeMode { get; set; } = string.Empty;
    public string CustomFontSize { get; set; } = string.Empty;
    public string TimeZoneDisplay { get; set; } = string.Empty;
    public string RegistryUseOfflineOnly { get; set; } = string.Empty;
    public string EnableLiveRegistryExperimental { get; set; } = string.Empty;
    public string ShowStatusBarDiagnostics { get; set; } = string.Empty;
}

public sealed class SettingsHelpIndex
{
    public string MaxEvents { get; set; } = string.Empty;
}

public sealed class ProcessCacheSettings
{
    public CachePolicy EventLog { get; set; } = new()
    {
        ProvisionalTtlMinutes = 720,
        AuthoritativeTtlMinutes = 1440,
        MaxEntries = 20000
    };

    public CachePolicy Etw { get; set; } = new()
    {
        ProvisionalTtlMinutes = 30,
        AuthoritativeTtlMinutes = 120,
        MaxEntries = 50000
    };

    /// <summary>ETW throttle: max events per second per category. Default stable value is 300. Set 0 to use built-in defaults (File 500, Registry/Network 300). Higher values reduce drops but may increase CPU/memory.</summary>
    public int EtwThrottleMaxPerSecond { get; set; } = 300;

    public int LongLivedTtlMinutes { get; set; } = 10080;

    public List<string> LongLivedProcessNames { get; set; } = new()
    {
        "svchost.exe",
        "lsass.exe",
        "wininit.exe",
        "winlogon.exe",
        "services.exe",
        "csrss.exe",
        "smss.exe"
    };
}

public sealed class CachePolicy
{
    public int ProvisionalTtlMinutes { get; set; }
    public int AuthoritativeTtlMinutes { get; set; }
    public int MaxEntries { get; set; }
}

public sealed class UiSettings
{
    public string FontSizeMode { get; set; } = "Medium";
    public int CustomFontSize { get; set; } = 16;
    /// <summary>Time zone for displaying timestamps: Local or UTC.</summary>
    public string TimeZoneDisplay { get; set; } = "Local";
    /// <summary>When true, do not use Live Registry (RegistrySearchProvider); use only Offline Hive / VSS for registry artifacts. Default true for forensic integrity (LIMITATIONS §2).</summary>
    public bool RegistryUseOfflineOnly { get; set; } = true;
    /// <summary>When true and RegistryUseOfflineOnly is false, allows Live Registry provider/search. Default false because Live mode is non-forensic. Use <see cref="RegistryLivePolicy.IsLiveRegistryEnabled(UiSettings?)"/> as the single gate.</summary>
    public bool EnableLiveRegistryExperimental { get; set; } = false;
    /// <summary>Optional default directory for export dialogs (Timeline CSV/JSON, etc.). Empty = use system default.</summary>
    public string ExportDefaultDirectory { get; set; } = string.Empty;
    /// <summary>Raw disk hive sources (drive, offset, size, name) for Offline Hive. Applied at startup; requires Administrator.</summary>
    public List<RawHiveSourceItem> RawHiveSources { get; set; } = new();
    /// <summary>If true, the first-time tip for Registry Search (Live non-forensic) has been shown.</summary>
    public bool HasSeenRegistrySearchTip { get; set; }
    /// <summary>If true, the first-time tip for Export Snapshot (manifest / knownLimitations) has been shown.</summary>
    public bool HasSeenExportSnapshotTip { get; set; }

    /// <summary>If true, show status bar diagnostics (PID cache, ETW throttle, UI backpressure).</summary>
    public bool ShowStatusBarDiagnostics { get; set; } = true;
}

/// <summary>One raw disk hive source: physical drive byte range to load as offline hive.</summary>
public sealed class RawHiveSourceItem
{
    public int DriveNumber { get; set; }
    public long OffsetBytes { get; set; }
    public int SizeBytes { get; set; }
    public string HiveName { get; set; } = "SYSTEM";
}

/// <summary>
/// Activity index (timeline) memory limits.
/// </summary>
public sealed class IndexSettings
{
    /// <summary>
    /// Maximum events to keep in memory; oldest are evicted. 0 = unbounded.
    /// </summary>
    public int MaxEvents { get; set; } = 200_000;
}
