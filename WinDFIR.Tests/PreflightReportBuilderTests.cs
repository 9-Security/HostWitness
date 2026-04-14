using System;
using System.IO;
using WinDFIR.Core.Settings;
using WinDFIR.Core.Snapshot;
using Xunit;

namespace WinDFIR.Tests;

public class PreflightReportBuilderTests
{
    [Fact]
    public void Build_WithWritableOutputDirectory_RecordsOperationalChecks()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "HostWitness_Preflight_" + Guid.NewGuid().ToString("N"));

        try
        {
            var report = PreflightReportBuilder.Build(
                CreateForensicStrictSettings(),
                executionContext: "ui_interactive",
                useVssSnapshots: false,
                enabledProviders: new[] { "TimelineProvider" },
                outputDirectory: outputDirectory,
                isAdministrator: true,
                isVssServiceRunning: true,
                generatedAtUtc: new DateTime(2026, 3, 19, 8, 0, 0, DateTimeKind.Utc));

            Assert.Equal("2026-03-19T08:00:00.0000000Z", report.GeneratedAtUtc);
            Assert.True(report.OutputDirectoryExists);
            Assert.True(report.OutputDirectoryWritable);
            Assert.NotNull(report.OutputVolumeRoot);
            Assert.NotNull(report.AvailableFreeSpaceBytes);
            Assert.False(report.HasErrors);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                try { Directory.Delete(outputDirectory, true); } catch { }
            }
        }
    }

    [Fact]
    public void Build_WithDefaultSettings_UsesForensicStrictProfileWithoutCustomWarning()
    {
        var report = PreflightReportBuilder.Build(
            new HostWitnessSettings(),
            executionContext: "ui_interactive",
            useVssSnapshots: false,
            isAdministrator: true,
            isVssServiceRunning: true,
            generatedAtUtc: new DateTime(2026, 3, 19, 8, 30, 0, DateTimeKind.Utc));

        Assert.Equal(CollectionMetadataBuilder.ForensicStrictProfile, report.ModeProfile);
        Assert.Equal(300, report.EtwThrottleMaxPerSecond);
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("Custom profile is active", StringComparison.Ordinal));
    }

    [Fact]
    public void MergeWarnings_PreservesReportWarningsAndDeduplicatesAdditionalWarnings()
    {
        var merged = PreflightReportBuilder.MergeWarnings(
            new[] { "Custom profile is active.", "Live Registry is enabled." },
            new[] { "Live Registry is enabled.", "Output directory is not writable." });

        Assert.Equal(
            new[]
            {
                "Custom profile is active.",
                "Live Registry is enabled.",
                "Output directory is not writable."
            },
            merged);
    }

    [Fact]
    public void Build_WithInvalidOutputDirectory_AddsError()
    {
        var invalidPath = string.Concat((char)0, "invalid");

        var report = PreflightReportBuilder.Build(
            CreateForensicStrictSettings(),
            executionContext: "ui_interactive",
            useVssSnapshots: false,
            outputDirectory: invalidPath,
            isAdministrator: true,
            isVssServiceRunning: true);

        Assert.True(report.HasErrors);
        Assert.False(report.OutputDirectoryWritable);
        Assert.Contains(report.Errors, error => error.StartsWith("Output directory preflight failed:", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WithLowFreeSpaceThreshold_AddsWarning()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "HostWitness_Preflight_" + Guid.NewGuid().ToString("N"));

        try
        {
            var report = PreflightReportBuilder.Build(
                CreateForensicStrictSettings(),
                executionContext: "ui_interactive",
                useVssSnapshots: false,
                outputDirectory: outputDirectory,
                isAdministrator: true,
                isVssServiceRunning: true,
                minimumRecommendedFreeSpaceBytes: long.MaxValue);

            Assert.True(report.HasWarnings);
            Assert.Contains(report.Warnings, warning => warning.Contains("recommended minimum", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                try { Directory.Delete(outputDirectory, true); } catch { }
            }
        }
    }

    private static HostWitnessSettings CreateForensicStrictSettings()
    {
        return new HostWitnessSettings
        {
            Ui = new UiSettings
            {
                RegistryUseOfflineOnly = true,
                EnableLiveRegistryExperimental = false,
                TimeZoneDisplay = "UTC"
            },
            ProcessCache = new ProcessCacheSettings
            {
                EventLog = new CachePolicy
                {
                    ProvisionalTtlMinutes = 720,
                    AuthoritativeTtlMinutes = 1440,
                    MaxEntries = 20_000
                },
                Etw = new CachePolicy
                {
                    ProvisionalTtlMinutes = 30,
                    AuthoritativeTtlMinutes = 120,
                    MaxEntries = 50_000
                },
                EtwThrottleMaxPerSecond = 300,
                LongLivedTtlMinutes = 10_080
            },
            Index = new IndexSettings
            {
                MaxEvents = 200_000
            }
        };
    }
}