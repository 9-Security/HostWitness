using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinDFIR.Core.Index;
using WinDFIR.Providers;
using WinDFIR.UI;
using Xunit;
using ActivityEvent = WinDFIR.Core.Entities.ActivityEvent;
using EvidenceRef = WinDFIR.Core.Entities.EvidenceRef;

namespace WinDFIR.Tests;

public class EtwAndIndexPressureTests
{
    [Fact]
    public void EtwBurstQueueDrops_AccumulateInThrottleStats_ForManifestTotals()
    {
        var p = new ETWMonitorProvider(16);
        p.ResetBurstReportGateForTest();
        p.AddBurstQueueDropsForTest(5);

        var stats = p.GetEtwThrottleStats();
        Assert.True(stats.TotalDrops.TryGetValue(ETWMonitorProvider.BurstQueueDropCategory, out var n));
        Assert.Equal(5, n);
    }

    [Fact]
    public void EtwBurst_LastReportedDrops_SurfaceBurstQueue_LikeThrottlePath()
    {
        var p = new ETWMonitorProvider(64);
        p.ResetBurstReportGateForTest();
        p.AddBurstQueueDropsForTest(1);

        var stats = p.GetEtwThrottleStats();
        Assert.True(stats.LastReportedDrops.TryGetValue(ETWMonitorProvider.BurstQueueDropCategory, out var last));
        Assert.Equal(1, last);
    }

    [Fact]
    public void EtwBurst_EmitBurstWarning_IncludesSystemEvent_WhenEventProducedSubscribed()
    {
        var p = new ETWMonitorProvider(64);
        p.ResetBurstReportGateForTest();
        var warnings = new List<ActivityEvent>();
        p.EventProduced += (_, e) =>
        {
            if (e.Fields.TryGetValue("Category", out var c) &&
                string.Equals(c?.ToString(), ETWMonitorProvider.BurstQueueDropCategory, StringComparison.Ordinal))
                warnings.Add(e);
        };

        p.AddBurstQueueDropsForTest(1);

        var w = Assert.Single(warnings);
        Assert.Equal("System", w.Category);
        Assert.Contains("ingest queue", w.Summary ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EtwIngest_DrainsQueuedCaptures_OnShutdown()
    {
        var p = new ETWMonitorProvider(512);
        long fileEvents = 0;
        p.EventProduced += (_, e) =>
        {
            if (string.Equals(e.Category, "File", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref fileEvents);
        };

        await p.StartAsync();
        var readySw = Stopwatch.StartNew();
        while (!p.IsIngestQueueReadyForTest() && readySw.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);
        Assert.True(p.IsIngestQueueReadyForTest());

        Assert.True(p.TryEnqueueCapturedRecordForTest(ETWMonitorProvider.CreateSyntheticKernelFileCaptureForTest(1)));
        Assert.True(p.TryEnqueueCapturedRecordForTest(ETWMonitorProvider.CreateSyntheticKernelFileCaptureForTest(2)));

        var sw = Stopwatch.StartNew();
        while (Interlocked.Read(ref fileEvents) < 2 && sw.Elapsed < TimeSpan.FromSeconds(8))
            await Task.Delay(25);

        await p.StopAsync();

        Assert.True(Interlocked.Read(ref fileEvents) >= 2);
    }

    [Fact]
    public async Task EtwIngest_QueueSaturation_RecordsBurstQueueDrops()
    {
        var p = new ETWMonitorProvider(2);
        await p.StartAsync();
        var readySw = Stopwatch.StartNew();
        while (!p.IsIngestQueueReadyForTest() && readySw.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);
        Assert.True(p.IsIngestQueueReadyForTest());

        var burstBefore = p.GetEtwThrottleStats().TotalDrops.GetValueOrDefault(ETWMonitorProvider.BurstQueueDropCategory);
        var fails = 0;
        for (var i = 0; i < 40; i++)
        {
            if (!p.TryEnqueueCapturedRecordForTest(ETWMonitorProvider.CreateSyntheticKernelFileCaptureForTest(i)))
                fails++;
        }

        await p.StopAsync();

        Assert.True(fails > 0);
        var burstAfter = p.GetEtwThrottleStats().TotalDrops.GetValueOrDefault(ETWMonitorProvider.BurstQueueDropCategory);
        Assert.True(burstAfter >= burstBefore + fails);
    }

    [Fact]
    public void BlockingCollection_AfterCompleteAdding_EnumeratorDrainsAllAcceptedItems()
    {
        var q = new BlockingCollection<int>(5);
        q.Add(1);
        q.Add(2);
        q.Add(3);
        q.CompleteAdding();
        Assert.Equal(3, q.GetConsumingEnumerable().Count());
    }

    [Fact]
    public void MainWindow_UiFlush_ReschedulesWhenPendingRemain()
    {
        Assert.True(MainWindow.ShouldScheduleAnotherUiFlush(true));
        Assert.False(MainWindow.ShouldScheduleAnotherUiFlush(false));
    }

    [Fact]
    public void InMemoryActivityIndex_EnforcesCapacity_WithExplicitEvictions()
    {
        var idx = new InMemoryActivityIndex(400);
        var t0 = DateTime.UtcNow;
        for (var i = 0; i < 520; i++)
        {
            idx.AddEvent(new ActivityEvent
            {
                Timestamp = t0.AddTicks(i),
                Category = "File",
                Action = "Open",
                Evidence = new List<EvidenceRef> { new("Test", $"ref-{i}", null, t0) },
                Summary = $"e{i}"
            });
        }

        Assert.True(idx.EventCount <= 400);
        Assert.True(idx.EvictedEvents > 0);
    }
}
