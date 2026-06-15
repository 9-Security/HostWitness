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

public class CrossSourceTaskTests
{
    // --- pure unit tests ---

    [Fact]
    public void NormalizeTaskPath_MakesComparableKeys()
    {
        Assert.Equal(@"\microsoft\windows\foo", CrossSourceTaskAnalyzer.NormalizeTaskPath(@"\Microsoft\Windows\Foo"));
        Assert.Equal(@"\microsoft\windows\foo", CrossSourceTaskAnalyzer.NormalizeTaskPath(@"Microsoft/Windows/Foo\"));
    }

    [Fact]
    public void TaskCacheItems_DecodeHexRenderedPath()
    {
        // TaskCache_Path is rendered as UTF-16LE hex by the offline provider.
        var hex = string.Join(" ", System.Text.Encoding.Unicode.GetBytes(@"\Microsoft\Windows\Evil")
            .Select(b => b.ToString("X2")));
        var evt = RegEvent(("TaskCache_Path", hex));

        var items = CrossSourceTaskAnalyzer.BuildTaskCacheItems(new[] { evt });
        var item = Assert.Single(items);
        Assert.Equal(@"\microsoft\windows\evil", item.Key);
    }

    [Fact]
    public void Analyze_TaskInCacheButNotOnDisk_FlaggedHidden()
    {
        var onDisk = new List<ActivityEvent>
        {
            TaskEvent(@"\Microsoft\Windows\Good")
        };
        var hexEvil = HexPath(@"\Microsoft\Windows\Evil");
        var hexGood = HexPath(@"\Microsoft\Windows\Good");
        var registry = new List<ActivityEvent>
        {
            RegEvent(("TaskCache_Path", hexGood)),
            RegEvent(("TaskCache_Path", hexEvil))   // present in TaskCache, no on-disk XML -> hidden
        };

        var index = new WinDFIR.Core.Index.InMemoryActivityIndex(0);
        foreach (var e in onDisk.Concat(registry))
            index.AddEvent(e);
        var anomalies = CrossSourceTaskAnalyzer.Analyze(index);

        var hidden = Assert.Single(anomalies, a => a.Fields["AnomalyKind"].ToString() == CrossSourceAnomalyDetector.MissingFromLive);
        Assert.Contains("evil", hidden.Summary!, StringComparison.OrdinalIgnoreCase);
    }

    // --- real-sample overlap validation (KAPE Tasks dir + SOFTWARE hive), gated ---

    private const string TasksDir = @"D:\cursor\KDFIR\KAPE\KAPE_Extracted\C\Windows\System32\Tasks";
    private const string SoftwareHive = @"D:\cursor\KDFIR\KAPE\KAPE_Extracted\C\Windows\System32\config\SOFTWARE";

    [Fact]
    public async Task RealSamples_OnDiskAndTaskCache_OverlapHeavily()
    {
        if (!Directory.Exists(TasksDir) || !File.Exists(SoftwareHive))
            return;

        // On-disk tasks via the Task-XML provider.
        var taskEvents = new List<ActivityEvent>();
        var tGate = new object();
        var taskProvider = new ScheduledTaskProvider(TasksDir);
        taskProvider.EventProduced += (_, e) => { lock (tGate) taskEvents.Add(e); };
        await taskProvider.StartAsync();
        await Wait(() => { lock (tGate) return taskEvents.Count > 0; }, 60);
        await Task.Delay(1500); // let enumeration finish
        await taskProvider.StopAsync();

        // TaskCache via the offline SOFTWARE hive.
        var regEvents = new List<ActivityEvent>();
        var rGate = new object();
        var hive = new OfflineHiveRegistryProvider();
        hive.AddHivePath(SoftwareHive);
        hive.EventProduced += (_, e) => { lock (rGate) regEvents.Add(e); };
        await hive.StartAsync();
        await Wait(() =>
        {
            List<ActivityEvent> cur; lock (rGate) cur = regEvents.ToList();
            return CrossSourceTaskAnalyzer.BuildTaskCacheItems(cur).Count >= 20;
        }, 120);
        await hive.StopAsync();

        List<ActivityEvent> tSnap, rSnap;
        lock (tGate) tSnap = taskEvents.ToList();
        lock (rGate) rSnap = regEvents.ToList();

        var onDisk = CrossSourceTaskAnalyzer.BuildOnDiskItems(tSnap);
        var cache = CrossSourceTaskAnalyzer.BuildTaskCacheItems(rSnap);
        Assert.NotEmpty(onDisk);
        Assert.NotEmpty(cache);

        // Most on-disk tasks should have a matching TaskCache entry (normalization aligns the keys).
        var cacheKeys = cache.Select(c => c.Key).ToHashSet();
        var matched = onDisk.Count(o => cacheKeys.Contains(o.Key));
        Assert.True(matched >= onDisk.Count / 2,
            $"Expected most on-disk tasks to match TaskCache; matched {matched}/{onDisk.Count}. Key normalization may be off.");
    }

    // --- helpers ---

    private static string HexPath(string p) =>
        string.Join(" ", System.Text.Encoding.Unicode.GetBytes(p).Select(b => b.ToString("X2")));

    private static ActivityEvent TaskEvent(string taskName) => new()
    {
        Timestamp = DateTime.UtcNow,
        Category = "Persistence",
        Action = "ScheduledTask",
        Evidence = new List<EvidenceRef> { new("ScheduledTask", taskName) },
        Fields = new Dictionary<string, object> { ["TaskName"] = taskName }
    };

    private static ActivityEvent RegEvent(params (string K, string V)[] fields)
    {
        var d = new Dictionary<string, object>();
        foreach (var (k, v) in fields) d[k] = v;
        return new ActivityEvent
        {
            Timestamp = DateTime.UtcNow,
            Category = "Registry",
            Action = "Query",
            Evidence = new List<EvidenceRef> { new("RegistryHive", "X:\\SOFTWARE") },
            Fields = d
        };
    }

    private static async Task Wait(Func<bool> cond, int seconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (cond()) return;
            await Task.Delay(100);
        }
    }
}
