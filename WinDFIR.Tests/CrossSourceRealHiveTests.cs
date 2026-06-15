using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinDFIR.Core.Analysis;
using WinDFIR.Core.Entities;
using WinDFIR.Providers;
using Xunit;

namespace WinDFIR.Tests;

// Validates the offline half of the cross-source service check against a REAL SYSTEM hive (KAPE sample):
// the raw hive parses into user-mode service items, and a planted "live" set missing one of them yields the
// MissingFromLive (hiding) anomaly. Gated on sample presence.
public class CrossSourceRealHiveTests
{
    private const string SystemHive = @"D:\cursor\KDFIR\KAPE\KAPE_Extracted\C\Windows\System32\config\SYSTEM";

    [Fact]
    public async Task RealSystemHive_OfflineServices_FeedDetector()
    {
        if (!File.Exists(SystemHive))
            return;

        var events = new List<ActivityEvent>();
        var gate = new object();
        var provider = new OfflineHiveRegistryProvider();
        provider.AddHivePath(SystemHive);
        provider.EventProduced += (_, e) => { lock (gate) events.Add(e); };

        await provider.StartAsync();
        // The recursive Services traversal emits over time and early entries are mostly kernel drivers;
        // wait until enough user-mode (Win32) service items have been recovered.
        await WaitUntilAsync(() =>
        {
            List<ActivityEvent> cur;
            lock (gate) cur = events.ToList();
            return CrossSourceServiceAnalyzer.BuildOfflineItems(cur).Count >= 10;
        }, TimeSpan.FromSeconds(120));
        await provider.StopAsync();

        List<ActivityEvent> snapshot;
        lock (gate) snapshot = events.ToList();

        var offlineItems = CrossSourceServiceAnalyzer.BuildOfflineItems(snapshot);
        Assert.NotEmpty(offlineItems); // real user-mode services recovered from the raw hive

        // Simulate a live API that hides exactly one real service -> detector must flag it MissingFromLive.
        var hidden = offlineItems[0];
        var liveView = offlineItems.Skip(1).ToList();

        var anomalies = CrossSourceAnomalyDetector.Compare("Service", liveView, offlineItems, compareValues: false);

        Assert.Contains(anomalies, a =>
            a.Kind == CrossSourceAnomalyDetector.MissingFromLive && a.Key == hidden.Key);
        // The only difference between the two sets is the one hidden item.
        Assert.Single(anomalies, a => a.Kind == CrossSourceAnomalyDetector.MissingFromLive);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return;
            await Task.Delay(100);
        }
    }
}
