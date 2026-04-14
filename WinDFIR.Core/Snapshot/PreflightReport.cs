using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinDFIR.Core.Settings;

namespace WinDFIR.Core.Snapshot;

public sealed class PreflightReport
{
    public string GeneratedAtUtc { get; init; } = DateTime.UtcNow.ToString("O");
    public string ExecutionContext { get; init; } = "unknown";
    public string ModeProfile { get; init; } = CollectionMetadataBuilder.CustomProfile;
    public bool IsAdministrator { get; init; }
    public bool VssServiceRunning { get; init; }
    public bool UseVssSnapshots { get; init; }
    public string RegistryMode { get; init; } = "offline_only";
    public string TimeZoneDisplay { get; init; } = "Local";
    public int IndexMaxEvents { get; init; }
    public int EtwThrottleMaxPerSecond { get; init; }
    public int RawHiveSourceCount { get; init; }
    public string[] EnabledProviders { get; init; } = Array.Empty<string>();
    public int? CollectSeconds { get; init; }
    public string? OutputDirectory { get; init; }
    public string? OutputVolumeRoot { get; init; }
    public bool? OutputDirectoryExists { get; init; }
    public bool? OutputDirectoryWritable { get; init; }
    public long? AvailableFreeSpaceBytes { get; init; }
    public long MinimumRecommendedFreeSpaceBytes { get; init; }
    public string[] Warnings { get; init; } = Array.Empty<string>();
    public string[] Errors { get; init; } = Array.Empty<string>();

    public bool HasWarnings => Warnings.Length > 0;
    public bool HasErrors => Errors.Length > 0;

    public Dictionary<string, object?> ToManifestDictionary()
    {
        return new Dictionary<string, object?>
        {
            ["generatedAtUtc"] = GeneratedAtUtc,
            ["executionContext"] = ExecutionContext,
            ["modeProfile"] = ModeProfile,
            ["isAdministrator"] = IsAdministrator,
            ["vssServiceRunning"] = VssServiceRunning,
            ["useVssSnapshots"] = UseVssSnapshots,
            ["registryMode"] = RegistryMode,
            ["timeZoneDisplay"] = TimeZoneDisplay,
            ["indexMaxEvents"] = IndexMaxEvents,
            ["etwThrottleMaxPerSecond"] = EtwThrottleMaxPerSecond,
            ["rawHiveSourceCount"] = RawHiveSourceCount,
            ["enabledProviders"] = EnabledProviders,
            ["collectSeconds"] = CollectSeconds,
            ["outputDirectory"] = OutputDirectory,
            ["outputVolumeRoot"] = OutputVolumeRoot,
            ["outputDirectoryExists"] = OutputDirectoryExists,
            ["outputDirectoryWritable"] = OutputDirectoryWritable,
            ["availableFreeSpaceBytes"] = AvailableFreeSpaceBytes,
            ["minimumRecommendedFreeSpaceBytes"] = MinimumRecommendedFreeSpaceBytes,
            ["warnings"] = Warnings,
            ["errors"] = Errors,
            ["hasWarnings"] = HasWarnings,
            ["hasErrors"] = HasErrors
        };
    }
}

public static class PreflightReportBuilder
{
    public const long DefaultMinimumRecommendedFreeSpaceBytes = 512L * 1024 * 1024;

    public static PreflightReport Build(
        HostWitnessSettings? settings,
        string executionContext,
        bool useVssSnapshots,
        IEnumerable<string>? enabledProviders = null,
        int? collectSeconds = null,
        string? outputDirectory = null,
        bool? isAdministrator = null,
        bool? isVssServiceRunning = null,
        DateTime? generatedAtUtc = null,
        long minimumRecommendedFreeSpaceBytes = DefaultMinimumRecommendedFreeSpaceBytes)
    {
        var normalized = settings ?? new HostWitnessSettings();
        var ui = normalized.Ui ?? new UiSettings();
        var processCache = normalized.ProcessCache ?? new ProcessCacheSettings();
        var index = normalized.Index ?? new IndexSettings();
        var modeProfile = CollectionMetadataBuilder.DetectModeProfile(normalized);
        var registryLiveEnabled = CollectionMetadataBuilder.IsLiveRegistryEnabled(normalized);
        var registryMode = registryLiveEnabled ? "live_non_forensic" : "offline_only";
        var providers = CollectionMetadataBuilder.NormalizeProviders(enabledProviders);
        var admin = isAdministrator ?? VssSnapshotService.IsRunningAsAdmin();
        var vssRunning = isVssServiceRunning ?? VssSnapshotService.IsVssServiceRunning();
        var warnings = new List<string>();
        var errors = new List<string>();

        string? normalizedOutputDirectory = null;
        string? outputVolumeRoot = null;
        bool? outputDirectoryExists = null;
        bool? outputDirectoryWritable = null;
        long? availableFreeSpaceBytes = null;

        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            ProbeOutputDirectory(
                outputDirectory,
                minimumRecommendedFreeSpaceBytes,
                warnings,
                errors,
                out normalizedOutputDirectory,
                out outputVolumeRoot,
                out outputDirectoryExists,
                out outputDirectoryWritable,
                out availableFreeSpaceBytes);
        }

        if (!admin && (useVssSnapshots || (ui.RawHiveSources?.Count ?? 0) > 0))
            warnings.Add("Administrator privileges are recommended or required for some configured collection paths.");

        if (useVssSnapshots && !vssRunning)
            warnings.Add("VSS-backed export is enabled, but the Volume Shadow Copy service is not running.");

        if (registryLiveEnabled)
            warnings.Add("Live Registry is enabled; this mode is non-forensic.");

        if (string.Equals(modeProfile, CollectionMetadataBuilder.CustomProfile, StringComparison.Ordinal))
            warnings.Add("Custom profile is active; stable baseline assumptions may not apply.");

        return new PreflightReport
        {
            GeneratedAtUtc = (generatedAtUtc ?? DateTime.UtcNow).ToString("O"),
            ExecutionContext = string.IsNullOrWhiteSpace(executionContext) ? "unknown" : executionContext,
            ModeProfile = modeProfile,
            IsAdministrator = admin,
            VssServiceRunning = vssRunning,
            UseVssSnapshots = useVssSnapshots,
            RegistryMode = registryMode,
            TimeZoneDisplay = string.IsNullOrWhiteSpace(ui.TimeZoneDisplay) ? "Local" : ui.TimeZoneDisplay,
            IndexMaxEvents = index.MaxEvents,
            EtwThrottleMaxPerSecond = processCache.EtwThrottleMaxPerSecond,
            RawHiveSourceCount = ui.RawHiveSources?.Count ?? 0,
            EnabledProviders = providers,
            CollectSeconds = collectSeconds,
            OutputDirectory = normalizedOutputDirectory,
            OutputVolumeRoot = outputVolumeRoot,
            OutputDirectoryExists = outputDirectoryExists,
            OutputDirectoryWritable = outputDirectoryWritable,
            AvailableFreeSpaceBytes = availableFreeSpaceBytes,
            MinimumRecommendedFreeSpaceBytes = minimumRecommendedFreeSpaceBytes,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            Errors = errors.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    public static string[] MergeWarnings(IEnumerable<string>? primaryWarnings, IEnumerable<string>? additionalWarnings = null)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AppendWarnings(primaryWarnings, merged, seen);
        AppendWarnings(additionalWarnings, merged, seen);
        return merged.ToArray();
    }

    private static void AppendWarnings(IEnumerable<string>? warnings, List<string> merged, HashSet<string> seen)
    {
        if (warnings == null)
            return;

        foreach (var warning in warnings)
        {
            if (string.IsNullOrWhiteSpace(warning) || !seen.Add(warning))
                continue;

            merged.Add(warning);
        }
    }

    private static void ProbeOutputDirectory(
        string outputDirectory,
        long minimumRecommendedFreeSpaceBytes,
        List<string> warnings,
        List<string> errors,
        out string? normalizedOutputDirectory,
        out string? outputVolumeRoot,
        out bool? outputDirectoryExists,
        out bool? outputDirectoryWritable,
        out long? availableFreeSpaceBytes)
    {
        normalizedOutputDirectory = null;
        outputVolumeRoot = null;
        outputDirectoryExists = null;
        outputDirectoryWritable = null;
        availableFreeSpaceBytes = null;

        try
        {
            normalizedOutputDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(normalizedOutputDirectory);
            outputDirectoryExists = Directory.Exists(normalizedOutputDirectory);

            outputVolumeRoot = Path.GetPathRoot(normalizedOutputDirectory);
            if (!string.IsNullOrWhiteSpace(outputVolumeRoot))
            {
                var drive = new DriveInfo(outputVolumeRoot);
                if (drive.IsReady)
                {
                    availableFreeSpaceBytes = drive.AvailableFreeSpace;
                    if (availableFreeSpaceBytes.Value < minimumRecommendedFreeSpaceBytes)
                    {
                        warnings.Add($"Available free space is below the recommended minimum ({minimumRecommendedFreeSpaceBytes} bytes).");
                    }
                }
            }

            var probeFile = Path.Combine(normalizedOutputDirectory, $".hostwitness_preflight_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probeFile, string.Empty);
            File.Delete(probeFile);
            outputDirectoryWritable = true;
        }
        catch (Exception ex)
        {
            normalizedOutputDirectory ??= outputDirectory;
            outputDirectoryWritable = false;
            errors.Add($"Output directory preflight failed: {ex.Message}");
        }
    }
}
