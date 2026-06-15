using System;
using System.Collections.Generic;
using System.Linq;
using WinDFIR.Core.Analysis;
using WinDFIR.Core.Entities;
using Xunit;

namespace WinDFIR.Tests;

public class CrossSourceAnomalyTests
{
    private static CrossSourceItem Item(string key, string? value = null) =>
        new() { Key = key, Display = key, Value = value };

    // --- detector ---

    [Fact]
    public void Compare_ItemOnlyInOffline_FlaggedMissingFromLive()
    {
        var live = new[] { Item("a"), Item("b") };
        var offline = new[] { Item("a"), Item("b"), Item("evilsvc") };

        var anomalies = CrossSourceAnomalyDetector.Compare("Service", live, offline, compareValues: false);

        var hidden = Assert.Single(anomalies);
        Assert.Equal(CrossSourceAnomalyDetector.MissingFromLive, hidden.Kind);
        Assert.Equal("evilsvc", hidden.Key);
        Assert.Equal(AnomalyLevel.Amber, hidden.Level);
    }

    [Fact]
    public void Compare_ItemOnlyInLive_FlaggedMissingFromOffline()
    {
        var live = new[] { Item("a"), Item("ghost") };
        var offline = new[] { Item("a") };

        var anomalies = CrossSourceAnomalyDetector.Compare("Service", live, offline, compareValues: false);

        Assert.Single(anomalies, x => x.Kind == CrossSourceAnomalyDetector.MissingFromOffline && x.Key == "ghost");
    }

    [Fact]
    public void Compare_AllMatch_NoAnomalies()
    {
        var live = new[] { Item("a"), Item("b") };
        var offline = new[] { Item("b"), Item("a") };
        Assert.Empty(CrossSourceAnomalyDetector.Compare("Service", live, offline, compareValues: false));
    }

    [Fact]
    public void Compare_ValueMismatch_FlaggedWhenEnabled()
    {
        var live = new[] { Item("svc", @"C:\windows\system32\svc.exe") };
        var offline = new[] { Item("svc", @"C:\temp\evil.exe") };

        var withValues = CrossSourceAnomalyDetector.Compare("Service", live, offline, compareValues: true);
        Assert.Single(withValues, x => x.Kind == CrossSourceAnomalyDetector.ValueMismatch);

        var withoutValues = CrossSourceAnomalyDetector.Compare("Service", live, offline, compareValues: false);
        Assert.Empty(withoutValues);
    }

    [Fact]
    public void Compare_KeyMatchIsCaseInsensitive()
    {
        var live = new[] { Item("WuauServ") };
        var offline = new[] { Item("wuauserv") };
        Assert.Empty(CrossSourceAnomalyDetector.Compare("Service", live, offline, compareValues: false));
    }

    // --- service analyzer item extraction ---

    [Fact]
    public void BuildOfflineItems_KeepsUserModeServices_DropsDrivers()
    {
        var events = new List<ActivityEvent>
        {
            ServiceRegEvent("wuauserv", imagePath: @"C:\Windows\system32\svchost.exe", typeLabel: "Share Process"),
            ServiceRegEvent("evilsvc", imagePath: @"C:\temp\evil.exe", typeLabel: "Own Process"),
            ServiceRegEvent("disk", imagePath: @"\SystemRoot\System32\drivers\disk.sys", typeLabel: "Kernel Driver"),
        };

        var items = CrossSourceServiceAnalyzer.BuildOfflineItems(events);

        Assert.Contains(items, i => i.Key == "wuauserv");
        Assert.Contains(items, i => i.Key == "evilsvc");
        Assert.DoesNotContain(items, i => i.Key == "disk"); // kernel driver excluded (WMI Win32_Service won't list it)
    }

    [Fact]
    public void BuildLiveItems_ReadsServiceNameAndImagePath()
    {
        var events = new[] { LiveServiceEvent("wuauserv", @"C:\Windows\system32\svchost.exe -k netsvcs") };
        var items = CrossSourceServiceAnalyzer.BuildLiveItems(events);
        var item = Assert.Single(items);
        Assert.Equal("wuauserv", item.Key);
        Assert.Contains("svchost.exe", item.Value);
    }

    private static ActivityEvent ServiceRegEvent(string name, string imagePath, string typeLabel) => new()
    {
        Timestamp = DateTime.UtcNow,
        Category = "Registry",
        Action = "Query",
        Evidence = new List<EvidenceRef> { new("RegistryHive", "X:\\SYSTEM") },
        Fields = new Dictionary<string, object>
        {
            ["QueryName"] = "Services",
            ["ServiceName"] = name,
            ["ServiceImagePath"] = imagePath,
            ["ServiceTypeLabel"] = typeLabel
        }
    };

    private static ActivityEvent LiveServiceEvent(string name, string imagePath) => new()
    {
        Timestamp = DateTime.UtcNow,
        Category = "Service",
        Action = "Query",
        Evidence = new List<EvidenceRef> { new("LiveService", "Win32_Service:" + name) },
        Fields = new Dictionary<string, object>
        {
            ["ServiceName"] = name,
            ["ImagePath"] = imagePath
        }
    };
}
