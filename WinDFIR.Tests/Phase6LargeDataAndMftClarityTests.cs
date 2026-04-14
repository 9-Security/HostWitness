using System.Text.Json;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.IO;
using WinDFIR.Core.Snapshot;
using Xunit;

namespace WinDFIR.Tests;

public class Phase6LargeDataAndMftClarityTests
{
    [Fact]
    public void RawDiskReader_PartialMftNote_IsNull_WhenNotTruncated()
    {
        var r = new RawDiskReader.MftReadResult(new byte[10], 1024, null, false, false, 10L);
        Assert.Null(RawDiskReader.GetPartialMftLoadOperatorNote(r));
    }

    [Fact]
    public void RawDiskReader_PartialMftNote_IncludesCapPercentAndLogicalSize_WhenTruncated()
    {
        var r = new RawDiskReader.MftReadResult(
            new byte[1000],
            1024,
            null,
            false,
            true,
            10_000L);
        var note = RawDiskReader.GetPartialMftLoadOperatorNote(r);
        Assert.NotNull(note);
        Assert.Contains("100 MB read cap", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1,000", note, StringComparison.Ordinal);
        Assert.Contains("10,000", note, StringComparison.Ordinal);
        Assert.Contains("10%", note, StringComparison.Ordinal);
    }

    [Fact]
    public void RawDiskReader_PartialMftNote_WhenLogicalSizeUnknown_StillExplainsCap()
    {
        var r = new RawDiskReader.MftReadResult(new byte[2048], 1024, null, false, true, null);
        var note = RawDiskReader.GetPartialMftLoadOperatorNote(r);
        Assert.NotNull(note);
        Assert.Contains("100 MB read cap", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2,048", note, StringComparison.Ordinal);
        Assert.Contains("could not be determined", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SnapshotExporter_LargeSyntheticIndex_RoundTripsTimelineCount()
    {
        const int n = 1500;
        var index = new InMemoryActivityIndex(n + 100);
        var t0 = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < n; i++)
        {
            index.AddEvent(new ActivityEvent
            {
                Timestamp = t0.AddMinutes(i),
                Category = "File",
                Action = "Open",
                Summary = $"evt-{i}",
                Fields = new Dictionary<string, object> { ["i"] = i.ToString() },
                Evidence = new List<EvidenceRef>(),
                Confidence = "High"
            });
        }

        var exporter = new SnapshotExporter { UseVssSnapshots = false };
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_Phase6Snap_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            await exporter.ExportAsync(index, tempDir, cancellationToken: default);

            var snapshotDir = Directory.GetDirectories(tempDir).Single();
            var loaded = SnapshotImporter.LoadFromFolder(snapshotDir);
            var loadedEvents = loaded.GetEventsByTimeRange(DateTime.MinValue, DateTime.MaxValue).ToList();
            Assert.Equal(n, loadedEvents.Count);
            Assert.Equal("evt-0", loadedEvents[0].Summary);
            Assert.Equal("evt-1499", loadedEvents[^1].Summary);

            var timelinePath = Path.Combine(snapshotDir, "timeline.json");
            await using var fs = File.OpenRead(timelinePath);
            using var doc = await JsonDocument.ParseAsync(fs);
            Assert.Equal(n, doc.RootElement.GetProperty("events").GetArrayLength());
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [Fact]
    public async Task SnapshotExporter_TruncationReporting_WhenSourceCountExceedsExported()
    {
        const int exported = 50;
        var index = new InMemoryActivityIndex(exported + 10);
        var t0 = DateTime.UtcNow;
        for (var i = 0; i < exported; i++)
        {
            index.AddEvent(new ActivityEvent
            {
                Timestamp = t0.AddTicks(i),
                Category = "Process",
                Action = "Start",
                Summary = $"p{i}",
                Evidence = new List<EvidenceRef>()
            });
        }

        var exporter = new SnapshotExporter { UseVssSnapshots = false };
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_Phase6Cap_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var options = new SnapshotExportOptions { SourceEventCount = 100L };
            await exporter.ExportAsync(index, tempDir, options, default);

            var snapshotDir = Directory.GetDirectories(tempDir).Single();
            var manifestPath = Path.Combine(snapshotDir, "manifest.json");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var summary = doc.RootElement.GetProperty("collectionSummary");
            Assert.Equal(100L, summary.GetProperty("sourceEventCount").GetInt64());
            Assert.Equal(exported, summary.GetProperty("exportedEventCount").GetInt32());
            Assert.Equal(SnapshotExporter.ExportMaxEvents, summary.GetProperty("eventCap").GetInt32());
            Assert.True(summary.GetProperty("wasEventCountCapped").GetBoolean());
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [Fact]
    public void SqliteIndexPersistence_LargeRoundTrip_PreservesCountAndFirstEvent()
    {
        const int n = 800;
        var index = new InMemoryActivityIndex(n + 50);
        var t0 = new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < n; i++)
        {
            index.AddEvent(new ActivityEvent
            {
                Timestamp = t0.AddSeconds(i),
                Category = i % 2 == 0 ? "File" : "Process",
                Action = "Query",
                Summary = $"sql-{i}",
                Fields = new Dictionary<string, object>(),
                Evidence = new List<EvidenceRef>()
            });
        }

        var dbPath = Path.Combine(Path.GetTempPath(), "HostWitness_Phase6Sqlite_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var written = SqliteIndexPersistence.Export(index, dbPath);
            Assert.Equal(n, written);

            var loaded = SqliteIndexPersistence.LoadEvents(dbPath);
            Assert.Equal(n, loaded.Count);
            Assert.Equal("sql-0", loaded[0].Summary);
            Assert.Equal("File", loaded[0].Category);
            Assert.Equal("sql-799", loaded[^1].Summary);
        }
        finally
        {
            try
            {
                if (File.Exists(dbPath))
                    File.Delete(dbPath);
            }
            catch
            {
                // ignore
            }
        }
    }
}
