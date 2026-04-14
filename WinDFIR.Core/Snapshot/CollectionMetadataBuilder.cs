using System;
using System.Collections.Generic;
using System.Linq;
using WinDFIR.Core.Settings;

namespace WinDFIR.Core.Snapshot;

/// <summary>
/// Builds shared collection metadata for snapshot manifests.
/// </summary>
public static class CollectionMetadataBuilder
{
    public const string ForensicStrictProfile = "forensic_strict";
    public const string TriageFastProfile = "triage_fast";
    public const string CustomProfile = "custom";

    public static Dictionary<string, object?> BuildBaseManifestExtras(
        HostWitnessSettings settings,
        string executionContext,
        bool useVssSnapshots,
        IEnumerable<string>? enabledProviders = null,
        int? collectSeconds = null,
        bool? isAdministrator = null,
        bool? isVssServiceRunning = null,
        DateTime? generatedAtUtc = null,
        PreflightReport? preflightReport = null)
    {
        var modeProfile = DetectModeProfile(settings);
        var registryLiveEnabled = IsLiveRegistryEnabled(settings);
        var registryMode = registryLiveEnabled ? "live_non_forensic" : "offline_only";
        var effectivePreflight = preflightReport ?? PreflightReportBuilder.Build(
            settings,
            executionContext,
            useVssSnapshots,
            enabledProviders,
            collectSeconds,
            outputDirectory: null,
            isAdministrator,
            isVssServiceRunning,
            generatedAtUtc);

        return new Dictionary<string, object?>
        {
            ["modeProfile"] = modeProfile,
            ["registryLiveEnabled"] = registryLiveEnabled,
            ["registryMode"] = registryMode,
            ["preflight"] = effectivePreflight.ToManifestDictionary()
        };
    }

    public static string DetectModeProfile(HostWitnessSettings? settings)
    {
        var normalized = settings ?? new HostWitnessSettings();
        var ui = normalized.Ui ?? new UiSettings();
        var processCache = normalized.ProcessCache ?? new ProcessCacheSettings();
        var index = normalized.Index ?? new IndexSettings();

        if (MatchesForensicStrict(ui, processCache, index))
            return ForensicStrictProfile;

        if (MatchesTriageFast(ui, processCache, index))
            return TriageFastProfile;

        return CustomProfile;
    }

    public static bool IsLiveRegistryEnabled(HostWitnessSettings? settings) =>
        RegistryLivePolicy.IsLiveRegistryEnabled(settings);

    internal static string[] NormalizeProviders(IEnumerable<string>? enabledProviders)
    {
        if (enabledProviders == null)
            return Array.Empty<string>();

        return enabledProviders
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MatchesForensicStrict(UiSettings ui, ProcessCacheSettings processCache, IndexSettings index)
    {
        return ui.RegistryUseOfflineOnly == true
            && ui.EnableLiveRegistryExperimental == false
            && processCache.EtwThrottleMaxPerSecond == 300
            && index.MaxEvents == 200_000
            && processCache.EventLog.ProvisionalTtlMinutes == 720
            && processCache.EventLog.AuthoritativeTtlMinutes == 1440
            && processCache.EventLog.MaxEntries == 20_000
            && processCache.Etw.ProvisionalTtlMinutes == 30
            && processCache.Etw.AuthoritativeTtlMinutes == 120
            && processCache.Etw.MaxEntries == 50_000
            && processCache.LongLivedTtlMinutes == 10_080;
    }

    private static bool MatchesTriageFast(UiSettings ui, ProcessCacheSettings processCache, IndexSettings index)
    {
        return ui.RegistryUseOfflineOnly == false
            && ui.EnableLiveRegistryExperimental == true
            && processCache.EtwThrottleMaxPerSecond == 800
            && index.MaxEvents == 100_000
            && processCache.EventLog.ProvisionalTtlMinutes == 240
            && processCache.EventLog.AuthoritativeTtlMinutes == 720
            && processCache.EventLog.MaxEntries == 12_000
            && processCache.Etw.ProvisionalTtlMinutes == 15
            && processCache.Etw.AuthoritativeTtlMinutes == 60
            && processCache.Etw.MaxEntries == 25_000
            && processCache.LongLivedTtlMinutes == 4_320;
    }
}
