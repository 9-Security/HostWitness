using System;
using System.Collections.Generic;
using System.Reflection;
using WinDFIR.Core.Settings;
using WinDFIR.Core.Snapshot;
using Xunit;

namespace WinDFIR.Tests;

public class CollectionMetadataBuilderTests
{
    [Fact]
    public void DetectModeProfile_DefaultSettings_ReturnsForensicStrict()
    {
        var settings = new HostWitnessSettings();

        var profile = CollectionMetadataBuilder.DetectModeProfile(settings);

        Assert.Equal(CollectionMetadataBuilder.ForensicStrictProfile, profile);
    }

    [Fact]
    public void DetectModeProfile_ForensicStrictSettings_ReturnsForensicStrict()
    {
        var settings = CreateForensicStrictSettings();

        var profile = CollectionMetadataBuilder.DetectModeProfile(settings);

        Assert.Equal(CollectionMetadataBuilder.ForensicStrictProfile, profile);
    }

    [Fact]
    public void DetectModeProfile_TriageFastSettings_ReturnsTriageFast()
    {
        var settings = CreateTriageFastSettings();

        var profile = CollectionMetadataBuilder.DetectModeProfile(settings);

        Assert.Equal(CollectionMetadataBuilder.TriageFastProfile, profile);
    }

    [Fact]
    public void Normalize_LegacyStableDefault_UpgradesToForensicStrict()
    {
        var settings = new HostWitnessSettings
        {
            Ui = new UiSettings
            {
                RegistryUseOfflineOnly = true,
                EnableLiveRegistryExperimental = false,
                TimeZoneDisplay = "Local"
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
                EtwThrottleMaxPerSecond = 0,
                LongLivedTtlMinutes = 10_080
            },
            Index = new IndexSettings
            {
                MaxEvents = 200_000
            }
        };

        var normalize = typeof(HostWitnessSettings).GetMethod("Normalize", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(normalize);

        var changed = Assert.IsType<bool>(normalize!.Invoke(null, new object[] { settings }));

        Assert.True(changed);
        Assert.Equal(300, settings.ProcessCache.EtwThrottleMaxPerSecond);
        Assert.Equal(CollectionMetadataBuilder.ForensicStrictProfile, CollectionMetadataBuilder.DetectModeProfile(settings));
    }

    [Fact]
    public void BuildBaseManifestExtras_AddsExpectedPreflightFields()
    {
        var settings = CreateTriageFastSettings();

        var extras = CollectionMetadataBuilder.BuildBaseManifestExtras(
            settings,
            executionContext: "agent_headless",
            useVssSnapshots: true,
            enabledProviders: new[] { "NetConnectionProvider", "EventLogProvider", "NetConnectionProvider" },
            collectSeconds: 45,
            isAdministrator: true,
            isVssServiceRunning: false,
            generatedAtUtc: new DateTime(2026, 3, 19, 1, 2, 3, DateTimeKind.Utc));

        Assert.Equal(CollectionMetadataBuilder.TriageFastProfile, extras["modeProfile"]);
        Assert.Equal("live_non_forensic", extras["registryMode"]);
        Assert.Equal(true, extras["registryLiveEnabled"]);

        var preflight = Assert.IsType<Dictionary<string, object?>>(extras["preflight"]);
        Assert.Equal("2026-03-19T01:02:03.0000000Z", preflight["generatedAtUtc"]);
        Assert.Equal("agent_headless", preflight["executionContext"]);
        Assert.Equal(CollectionMetadataBuilder.TriageFastProfile, preflight["modeProfile"]);
        Assert.Equal(true, preflight["isAdministrator"]);
        Assert.Equal(false, preflight["vssServiceRunning"]);
        Assert.Equal(true, preflight["useVssSnapshots"]);
        Assert.Equal("live_non_forensic", preflight["registryMode"]);
        Assert.Equal("Local", preflight["timeZoneDisplay"]);
        Assert.Equal(100_000, preflight["indexMaxEvents"]);
        Assert.Equal(800, preflight["etwThrottleMaxPerSecond"]);
        Assert.Equal(0, preflight["rawHiveSourceCount"]);
        Assert.Equal(45, preflight["collectSeconds"]);

        var enabledProviders = Assert.IsType<string[]>(preflight["enabledProviders"]);
        Assert.Equal(new[] { "EventLogProvider", "NetConnectionProvider" }, enabledProviders);
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

    private static HostWitnessSettings CreateTriageFastSettings()
    {
        return new HostWitnessSettings
        {
            Ui = new UiSettings
            {
                RegistryUseOfflineOnly = false,
                EnableLiveRegistryExperimental = true,
                TimeZoneDisplay = "Local"
            },
            ProcessCache = new ProcessCacheSettings
            {
                EventLog = new CachePolicy
                {
                    ProvisionalTtlMinutes = 240,
                    AuthoritativeTtlMinutes = 720,
                    MaxEntries = 12_000
                },
                Etw = new CachePolicy
                {
                    ProvisionalTtlMinutes = 15,
                    AuthoritativeTtlMinutes = 60,
                    MaxEntries = 25_000
                },
                EtwThrottleMaxPerSecond = 800,
                LongLivedTtlMinutes = 4_320
            },
            Index = new IndexSettings
            {
                MaxEvents = 100_000
            }
        };
    }
}