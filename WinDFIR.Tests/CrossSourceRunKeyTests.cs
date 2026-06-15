using System;
using System.Collections.Generic;
using System.Linq;
using WinDFIR.Core.Analysis;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using Xunit;

namespace WinDFIR.Tests;

public class CrossSourceRunKeyTests
{
    [Theory]
    [InlineData("System Run Key", "HKLM")]
    [InlineData("User Run Key", "HKCU")]
    [InlineData("Run", "HKLM")]
    [InlineData("User Run", "HKCU")]
    [InlineData("RunOnce", null)]   // not compared (live provider doesn't cover RunOnce)
    [InlineData("Services", null)]
    public void MapRunScope_MapsComparableRunScopes(string queryName, string? expected)
    {
        Assert.Equal(expected, CrossSourceRunKeyAnalyzer.MapRunScope(queryName));
    }

    [Fact]
    public void BuildItems_SplitsByMode()
    {
        var events = new[]
        {
            RunEvent("System Run Key", "Updater", @"C:\u.exe", offline: false),
            RunEvent("Run", "Updater", @"C:\u.exe", offline: true),
            RunEvent("Run", "Evil", @"C:\evil.exe", offline: true)
        };

        var live = CrossSourceRunKeyAnalyzer.BuildItems(events, isOffline: false);
        var offline = CrossSourceRunKeyAnalyzer.BuildItems(events, isOffline: true);

        Assert.Single(live);
        Assert.Equal(2, offline.Count);
        Assert.Contains(live, i => i.Key == "hklm|updater");
    }

    [Fact]
    public void Analyze_RunEntryInHiveButNotLive_FlaggedHidden()
    {
        var index = new InMemoryActivityIndex(0);
        index.AddEvent(RunEvent("System Run Key", "Updater", @"C:\u.exe", offline: false));   // live HKLM Run
        index.AddEvent(RunEvent("Run", "Updater", @"C:\u.exe", offline: true));                // offline HKLM Run
        index.AddEvent(RunEvent("Run", "EvilPersist", @"C:\evil.exe", offline: true));         // offline-only -> hidden

        var anomalies = CrossSourceRunKeyAnalyzer.Analyze(index);

        var hidden = Assert.Single(anomalies, a => a.Fields["AnomalyKind"].ToString() == CrossSourceAnomalyDetector.MissingFromLive);
        Assert.Contains("evilpersist", hidden.Fields["Key"].ToString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_NoLiveRegistry_NoOp()
    {
        var index = new InMemoryActivityIndex(0);
        index.AddEvent(RunEvent("Run", "Updater", @"C:\u.exe", offline: true));   // offline only, no live
        Assert.Empty(CrossSourceRunKeyAnalyzer.Analyze(index));
    }

    private static ActivityEvent RunEvent(string queryName, string valueName, string valueData, bool offline)
    {
        var f = new Dictionary<string, object>
        {
            ["QueryName"] = queryName,
            ["ValueName"] = valueName,
            ["ValueData"] = valueData
        };
        if (offline) f["Mode"] = "Offline";
        return new ActivityEvent
        {
            Timestamp = DateTime.UtcNow,
            Category = "Registry",
            Action = "Query",
            Evidence = new List<EvidenceRef> { new("RegistryHive", "X:\\SOFTWARE") },
            Fields = f
        };
    }
}
