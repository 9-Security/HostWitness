using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using WinDFIR.Core.Settings;

namespace WinDFIR.UI.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private sealed class SettingsProfileDto
    {
        public string Name { get; set; } = string.Empty;
        public bool RegistryUseOfflineOnly { get; set; }
        public bool EnableLiveRegistryExperimental { get; set; }
        public bool ShowStatusBarDiagnostics { get; set; } = true;
        public string EtwThrottleMaxPerSecond { get; set; } = string.Empty;
        public string IndexMaxEvents { get; set; } = string.Empty;
        public string EventLogProvisionalTtlMinutes { get; set; } = string.Empty;
        public string EventLogAuthoritativeTtlMinutes { get; set; } = string.Empty;
        public string EventLogMaxEntries { get; set; } = string.Empty;
        public string EtwProvisionalTtlMinutes { get; set; } = string.Empty;
        public string EtwAuthoritativeTtlMinutes { get; set; } = string.Empty;
        public string EtwMaxEntries { get; set; } = string.Empty;
        public string LongLivedTtlMinutes { get; set; } = string.Empty;
    }

    private HostWitnessSettings _settings;
    private string _selectedFontSizeOption = "Medium";
    private string _customFontSize = string.Empty;
    private bool _isCustomFontSize;

    public SettingsViewModel()
    {
        _settings = HostWitnessSettings.Load();
        LoadFromSettings();
    }

    public string EventLogProvisionalTtlMinutes { get; set; } = string.Empty;
    public string EventLogAuthoritativeTtlMinutes { get; set; } = string.Empty;
    public string EventLogMaxEntries { get; set; } = string.Empty;
    public string EtwProvisionalTtlMinutes { get; set; } = string.Empty;
    public string EtwAuthoritativeTtlMinutes { get; set; } = string.Empty;
    public string EtwMaxEntries { get; set; } = string.Empty;
    public string EtwThrottleMaxPerSecond { get; set; } = string.Empty;
    public string LongLivedTtlMinutes { get; set; } = string.Empty;
    public string LongLivedProcessNamesText { get; set; } = string.Empty;
    private string _indexMaxEvents = string.Empty;
    public string IndexMaxEvents
    {
        get => _indexMaxEvents;
        set
        {
            if (_indexMaxEvents == value) return;
            _indexMaxEvents = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IndexMaxEventsUnboundedWarning));
        }
    }
    /// <summary>Warning when value is 0 (unbounded) for OOM mitigation visibility.</summary>
    public string IndexMaxEventsUnboundedWarning => int.TryParse(IndexMaxEvents?.Trim(), out var v) && v == 0
        ? "Warning: Unbounded (0) may cause high memory use or OOM. Prefer a cap (e.g. 200000)."
        : "";
    public string SelectedTimeZoneDisplay { get; set; } = "Local";
    public bool RegistryUseOfflineOnly { get; set; }
    public bool EnableLiveRegistryExperimental { get; set; }
    public bool ShowStatusBarDiagnostics { get; set; } = true;
    public string ExportDefaultDirectory { get; set; } = string.Empty;
    public string ShowStatusBarDiagnosticsHelp => _settings.Help.Ui?.ShowStatusBarDiagnostics
        ?? "Show status bar diagnostics (PID cache, ETW throttle, UI queue backpressure). Restart to apply.";
    /// <summary>Raw disk hive list: one line per entry "DriveNumber,OffsetBytes,SizeBytes,HiveName" (e.g. 0,0,409600,SYSTEM). Requires Administrator; restart to apply.</summary>
    public string RawHiveSourcesText { get; set; } = string.Empty;
    public IReadOnlyList<FontSizeOption> FontSizeOptions { get; } = new[]
    {
        new FontSizeOption("小 (12)", "Small"),
        new FontSizeOption("中 (16)", "Medium"),
        new FontSizeOption("大 (20)", "Large"),
        new FontSizeOption("自訂", "Custom")
    };
    public IReadOnlyList<FontSizeOption> TimeZoneDisplayOptions { get; } = new[]
    {
        new FontSizeOption("Local (本地)", "Local"),
        new FontSizeOption("UTC", "UTC")
    };

    public string SelectedFontSizeOption
    {
        get => _selectedFontSizeOption;
        set
        {
            if (_selectedFontSizeOption == value)
                return;

            _selectedFontSizeOption = value;
            IsCustomFontSize = string.Equals(_selectedFontSizeOption, "Custom", StringComparison.OrdinalIgnoreCase);
            OnPropertyChanged();
        }
    }

    public string CustomFontSize
    {
        get => _customFontSize;
        set
        {
            if (_customFontSize == value)
                return;

            _customFontSize = value;
            OnPropertyChanged();
        }
    }

    public bool IsCustomFontSize
    {
        get => _isCustomFontSize;
        private set
        {
            if (_isCustomFontSize == value)
                return;

            _isCustomFontSize = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> Notes => _settings.Help.Notes;
    public string EventLogHelp => _settings.Help.ProcessCache.EventLog;
    public string EtwHelp => _settings.Help.ProcessCache.Etw;
    public string EtwThrottleMaxPerSecondHelp => _settings.Help.ProcessCache?.EtwThrottleMaxPerSecond ?? "ETW throttle cap: max events per second per category. Default stable value is 300. Set 0 to use built-in defaults (File 500, Registry/Network 300). Raising may increase CPU and memory; restart to apply.";
    public string LongLivedTtlHelp => _settings.Help.ProcessCache.LongLivedTtlMinutes;
    public string LongLivedNamesHelp => _settings.Help.ProcessCache.LongLivedProcessNames;
    public string FontSizeModeHelp => _settings.Help.Ui.FontSizeMode;
    public string CustomFontSizeHelp => _settings.Help.Ui.CustomFontSize;
    public string TimeZoneDisplayHelp => _settings.Help.Ui.TimeZoneDisplay ?? "Timestamp display: Local or UTC (Timeline and other views). Restart to apply.";
    public string RegistryUseOfflineOnlyHelp => _settings.Help.Ui.RegistryUseOfflineOnly ?? "Forensic-safe default. Disable Live Registry API by default.";
    public string EnableLiveRegistryExperimentalHelp => _settings.Help.Ui.EnableLiveRegistryExperimental ?? "Enable Live Registry search/collection (non-forensic).";
    public string IndexMaxEventsHelp => _settings.Help.Index?.MaxEvents ?? "Max events to keep in memory; oldest are evicted. 0 = unbounded.";

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Save(out string? error)
    {
        error = null;

        var processCache = _settings.ProcessCache;
        processCache.EventLog.ProvisionalTtlMinutes = NormalizeInt(EventLogProvisionalTtlMinutes, processCache.EventLog.ProvisionalTtlMinutes);
        processCache.EventLog.AuthoritativeTtlMinutes = NormalizeInt(EventLogAuthoritativeTtlMinutes, processCache.EventLog.AuthoritativeTtlMinutes);
        processCache.EventLog.MaxEntries = NormalizeInt(EventLogMaxEntries, processCache.EventLog.MaxEntries);

        processCache.Etw.ProvisionalTtlMinutes = NormalizeInt(EtwProvisionalTtlMinutes, processCache.Etw.ProvisionalTtlMinutes);
        processCache.Etw.AuthoritativeTtlMinutes = NormalizeInt(EtwAuthoritativeTtlMinutes, processCache.Etw.AuthoritativeTtlMinutes);
        processCache.Etw.MaxEntries = NormalizeInt(EtwMaxEntries, processCache.Etw.MaxEntries);
        processCache.EtwThrottleMaxPerSecond = NormalizeInt(EtwThrottleMaxPerSecond, processCache.EtwThrottleMaxPerSecond);

        processCache.LongLivedTtlMinutes = NormalizeInt(LongLivedTtlMinutes, processCache.LongLivedTtlMinutes);
        processCache.LongLivedProcessNames = ParseLongLivedNames(LongLivedProcessNamesText);

        _settings.Ui.FontSizeMode = SelectedFontSizeOption;
        _settings.Ui.CustomFontSize = NormalizeFontSize(CustomFontSize, _settings.Ui.CustomFontSize);
        _settings.Ui.TimeZoneDisplay = string.Equals(SelectedTimeZoneDisplay, "UTC", StringComparison.OrdinalIgnoreCase) ? "UTC" : "Local";
        _settings.Ui.RegistryUseOfflineOnly = RegistryUseOfflineOnly;
        _settings.Ui.EnableLiveRegistryExperimental = RegistryUseOfflineOnly ? false : EnableLiveRegistryExperimental;
        _settings.Ui.ShowStatusBarDiagnostics = ShowStatusBarDiagnostics;
        _settings.Ui.ExportDefaultDirectory = ExportDefaultDirectory?.Trim() ?? string.Empty;
        _settings.Ui.RawHiveSources = ParseRawHiveSources(RawHiveSourcesText);

        _settings.Index ??= new IndexSettings();
        _settings.Index.MaxEvents = NormalizeInt(IndexMaxEvents, _settings.Index.MaxEvents);
        if (_settings.Index.MaxEvents < 0)
            _settings.Index.MaxEvents = 0;

        if (!HostWitnessSettings.Save(_settings))
        {
            error = "Failed to save settings. Please check file permissions.";
            return false;
        }

        return true;
    }

    public void ApplyForensicStrictProfile()
    {
        // Forensic-first defaults: offline registry only, conservative throttling, bounded memory.
        RegistryUseOfflineOnly = true;
        EnableLiveRegistryExperimental = false;
        ShowStatusBarDiagnostics = false;
        EtwThrottleMaxPerSecond = "300";
        IndexMaxEvents = "200000";
        EventLogProvisionalTtlMinutes = "720";
        EventLogAuthoritativeTtlMinutes = "1440";
        EventLogMaxEntries = "20000";
        EtwProvisionalTtlMinutes = "30";
        EtwAuthoritativeTtlMinutes = "120";
        EtwMaxEntries = "50000";
        LongLivedTtlMinutes = "10080";
        OnPropertyChanged(string.Empty);
    }

    public void ApplyTriageFastProfile()
    {
        // Triage profile: prioritize responsiveness and wider live visibility.
        RegistryUseOfflineOnly = false;
        EnableLiveRegistryExperimental = true;
        ShowStatusBarDiagnostics = true;
        EtwThrottleMaxPerSecond = "800";
        IndexMaxEvents = "100000";
        EventLogProvisionalTtlMinutes = "240";
        EventLogAuthoritativeTtlMinutes = "720";
        EventLogMaxEntries = "12000";
        EtwProvisionalTtlMinutes = "15";
        EtwAuthoritativeTtlMinutes = "60";
        EtwMaxEntries = "25000";
        LongLivedTtlMinutes = "4320";
        OnPropertyChanged(string.Empty);
    }

    public bool ExportProfile(string filePath, out string? error)
    {
        error = null;
        try
        {
            var dto = new SettingsProfileDto
            {
                Name = "custom",
                RegistryUseOfflineOnly = RegistryUseOfflineOnly,
                EnableLiveRegistryExperimental = EnableLiveRegistryExperimental,
                ShowStatusBarDiagnostics = ShowStatusBarDiagnostics,
                EtwThrottleMaxPerSecond = EtwThrottleMaxPerSecond,
                IndexMaxEvents = IndexMaxEvents,
                EventLogProvisionalTtlMinutes = EventLogProvisionalTtlMinutes,
                EventLogAuthoritativeTtlMinutes = EventLogAuthoritativeTtlMinutes,
                EventLogMaxEntries = EventLogMaxEntries,
                EtwProvisionalTtlMinutes = EtwProvisionalTtlMinutes,
                EtwAuthoritativeTtlMinutes = EtwAuthoritativeTtlMinutes,
                EtwMaxEntries = EtwMaxEntries,
                LongLivedTtlMinutes = LongLivedTtlMinutes
            };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool ImportProfile(string filePath, out string? error)
    {
        error = null;
        try
        {
            var json = File.ReadAllText(filePath);
            var dto = JsonSerializer.Deserialize<SettingsProfileDto>(json);
            if (dto == null)
            {
                error = "Invalid profile content.";
                return false;
            }

            RegistryUseOfflineOnly = dto.RegistryUseOfflineOnly;
            EnableLiveRegistryExperimental = dto.EnableLiveRegistryExperimental;
            ShowStatusBarDiagnostics = dto.ShowStatusBarDiagnostics;
            EtwThrottleMaxPerSecond = dto.EtwThrottleMaxPerSecond ?? EtwThrottleMaxPerSecond;
            IndexMaxEvents = dto.IndexMaxEvents ?? IndexMaxEvents;
            EventLogProvisionalTtlMinutes = dto.EventLogProvisionalTtlMinutes ?? EventLogProvisionalTtlMinutes;
            EventLogAuthoritativeTtlMinutes = dto.EventLogAuthoritativeTtlMinutes ?? EventLogAuthoritativeTtlMinutes;
            EventLogMaxEntries = dto.EventLogMaxEntries ?? EventLogMaxEntries;
            EtwProvisionalTtlMinutes = dto.EtwProvisionalTtlMinutes ?? EtwProvisionalTtlMinutes;
            EtwAuthoritativeTtlMinutes = dto.EtwAuthoritativeTtlMinutes ?? EtwAuthoritativeTtlMinutes;
            EtwMaxEntries = dto.EtwMaxEntries ?? EtwMaxEntries;
            LongLivedTtlMinutes = dto.LongLivedTtlMinutes ?? LongLivedTtlMinutes;
            OnPropertyChanged(string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void LoadFromSettings()
    {
        var processCache = _settings.ProcessCache;
        EventLogProvisionalTtlMinutes = processCache.EventLog.ProvisionalTtlMinutes.ToString();
        EventLogAuthoritativeTtlMinutes = processCache.EventLog.AuthoritativeTtlMinutes.ToString();
        EventLogMaxEntries = processCache.EventLog.MaxEntries.ToString();
        EtwProvisionalTtlMinutes = processCache.Etw.ProvisionalTtlMinutes.ToString();
        EtwAuthoritativeTtlMinutes = processCache.Etw.AuthoritativeTtlMinutes.ToString();
        EtwMaxEntries = processCache.Etw.MaxEntries.ToString();
        EtwThrottleMaxPerSecond = (processCache.EtwThrottleMaxPerSecond).ToString();
        LongLivedTtlMinutes = processCache.LongLivedTtlMinutes.ToString();
        LongLivedProcessNamesText = string.Join(Environment.NewLine, processCache.LongLivedProcessNames);
        _indexMaxEvents = (_settings.Index?.MaxEvents ?? 200_000).ToString();
        SelectedFontSizeOption = NormalizeFontSizeMode(_settings.Ui.FontSizeMode);
        CustomFontSize = _settings.Ui.CustomFontSize.ToString();
        SelectedTimeZoneDisplay = string.Equals(_settings.Ui.TimeZoneDisplay?.Trim(), "UTC", StringComparison.OrdinalIgnoreCase) ? "UTC" : "Local";
        RegistryUseOfflineOnly = _settings.Ui.RegistryUseOfflineOnly;
        EnableLiveRegistryExperimental = _settings.Ui.EnableLiveRegistryExperimental;
        ShowStatusBarDiagnostics = _settings.Ui.ShowStatusBarDiagnostics;
        ExportDefaultDirectory = _settings.Ui.ExportDefaultDirectory ?? string.Empty;
        RawHiveSourcesText = FormatRawHiveSources(_settings.Ui.RawHiveSources);

        OnPropertyChanged(string.Empty);
    }

    private static int NormalizeInt(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        if (!int.TryParse(value.Trim(), out var parsed))
            return fallback;

        if (parsed < 0)
            return 0;

        return parsed;
    }

    private static string FormatRawHiveSources(List<RawHiveSourceItem>? list)
    {
        if (list == null || list.Count == 0) return string.Empty;
        return string.Join(Environment.NewLine, list.Select(r => $"{r.DriveNumber},{r.OffsetBytes},{r.SizeBytes},{r.HiveName ?? "SYSTEM"}"));
    }

    private static List<RawHiveSourceItem> ParseRawHiveSources(string? text)
    {
        var list = new List<RawHiveSourceItem>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length < 3) continue;
            if (!int.TryParse(parts[0].Trim(), out var drive) || !long.TryParse(parts[1].Trim(), out var offset) || !int.TryParse(parts[2].Trim(), out var size) || size <= 0)
                continue;
            var name = parts.Length >= 4 ? parts[3].Trim() : "SYSTEM";
            if (string.IsNullOrEmpty(name)) name = "SYSTEM";
            list.Add(new RawHiveSourceItem { DriveNumber = drive, OffsetBytes = offset, SizeBytes = size, HiveName = name });
        }
        return list;
    }

    private static List<string> ParseLongLivedNames(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static string NormalizeFontSizeMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Medium";

        return value.Trim().ToLowerInvariant() switch
        {
            "small" => "Small",
            "large" => "Large",
            "custom" => "Custom",
            _ => "Medium"
        };
    }

    private static int NormalizeFontSize(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        if (!int.TryParse(value.Trim(), out var parsed))
            return fallback;

        if (parsed < 8)
            return 8;

        if (parsed > 72)
            return 72;

        return parsed;
    }
}

public sealed record FontSizeOption(string Label, string Value);
