using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinDFIR.Core.Index;
using WinDFIR.Core.Snapshot;
using ActivityEvent = WinDFIR.Core.Entities.ActivityEvent;
using EvidenceRef = WinDFIR.Core.Entities.EvidenceRef;
using Xunit;
using Xunit.Abstractions;

namespace WinDFIR.Tests;

/// <summary>
/// Bounded wall-clock measurements for a single code path each. Subject to GC/OS jitter; use median of several iterations.
/// Not a substitute for a profiler; documents before/after for small, safe Core changes.
/// </summary>
public sealed class CorePerfScopedMeasurements
{
    private readonly ITestOutputHelper _output;

    public CorePerfScopedMeasurements(ITestOutputHelper output) => _output = output;

    private static long Median(long[] arr)
    {
        Array.Sort(arr);
        return arr[arr.Length / 2];
    }

    private static long TimeMs(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    /// <summary>Hot path: full timeline scan used by SQLite export and snapshot export materialization.</summary>
    [Fact]
    public void Measure_GetEventsByTimeRange_full_scan_100k_bounded()
    {
        const int n = 100_000;
        const int iterations = 5;
        var index = new InMemoryActivityIndex(0);
        var t0 = DateTime.UtcNow;
        for (var i = 0; i < n; i++)
        {
            index.AddEvent(new ActivityEvent
            {
                Timestamp = t0.AddTicks(i),
                Category = "Perf",
                Action = "Tick",
                Summary = "s",
                Evidence = new List<EvidenceRef>()
            });
        }

        var warmupMs = TimeMs(() =>
        {
            var c = 0;
            foreach (var _ in index.GetEventsByTimeRange(DateTime.MinValue, DateTime.MaxValue))
                c++;
            Assert.Equal(n, c);
        });

        var samples = new long[iterations];
        for (var i = 0; i < iterations; i++)
        {
            samples[i] = TimeMs(() =>
            {
                var c = 0;
                foreach (var _ in index.GetEventsByTimeRange(DateTime.MinValue, DateTime.MaxValue))
                    c++;
                Assert.Equal(n, c);
            });
        }

        var med = Median(samples);
        _output.WriteLine(
            $"PERF_GETEVENTS_TIMERANGE n={n} warmup_ms={warmupMs} median_ms={med} iterations={iterations} samples_ms={string.Join(',', samples)}");
    }

    /// <summary>End-to-end SQLite export includes full index enumeration + batched inserts.</summary>
    [Fact]
    public void Measure_SqliteIndexPersistence_export_12k_bounded()
    {
        const int n = 12_000;
        const int iterations = 3;
        var index = new InMemoryActivityIndex(0);
        var t0 = DateTime.UtcNow;
        for (var i = 0; i < n; i++)
        {
            index.AddEvent(new ActivityEvent
            {
                Timestamp = t0.AddMinutes(i),
                Category = "Perf",
                Action = "Row",
                Summary = $"e{i}",
                Evidence = new List<EvidenceRef>()
            });
        }

        _ = TimeMs(() =>
        {
            var p = Path.Combine(Path.GetTempPath(), "HostWitness_PerfSqlite_w_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var w = SqliteIndexPersistence.Export(index, p);
                Assert.Equal(n, w);
            }
            finally
            {
                try
                {
                    if (File.Exists(p))
                        File.Delete(p);
                }
                catch
                {
                    // ignore
                }
            }
        });

        var samples = new long[iterations];
        for (var i = 0; i < iterations; i++)
        {
            samples[i] = TimeMs(() =>
            {
                var p = Path.Combine(Path.GetTempPath(), $"HostWitness_PerfSqlite_{i}_" + Guid.NewGuid().ToString("N") + ".db");
                try
                {
                    var w = SqliteIndexPersistence.Export(index, p);
                    Assert.Equal(n, w);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(p))
                            File.Delete(p);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            });
        }

        var med = Median(samples);
        _output.WriteLine(
            $"PERF_SQLITE_EXPORT n={n} median_ms={med} iterations={iterations} samples_ms={string.Join(',', samples)}");
    }

    /// <summary>Snapshot import: many events each with multiple raw/ evidence refs (path resolution inner loop).</summary>
    [Fact]
    public void Measure_SnapshotImporter_timeline_many_raw_evidence_refs_bounded()
    {
        const int eventCount = 800;
        const int evidencePerEvent = 12;
        const int iterations = 3;

        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_PerfSnap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var timelinePath = Path.Combine(tempDir, "timeline.json");
        try
        {
            BuildTimelineJsonFile(timelinePath, eventCount, evidencePerEvent);

            _ = TimeMs(() =>
            {
                var idx = SnapshotImporter.LoadFromFolder(tempDir);
                Assert.Equal(eventCount, idx.EventCount);
            });

            var samples = new long[iterations];
            for (var i = 0; i < iterations; i++)
            {
                samples[i] = TimeMs(() =>
                {
                    var idx = SnapshotImporter.LoadFromFolder(tempDir);
                    Assert.Equal(eventCount, idx.EventCount);
                });
            }

            var med = Median(samples);
            _output.WriteLine(
                $"PERF_SNAPSHOT_IMPORT events={eventCount} evidence_per_event={evidencePerEvent} total_refs={eventCount * evidencePerEvent} median_ms={med} iterations={iterations} samples_ms={string.Join(',', samples)}");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void BuildTimelineJsonFile(string path, int eventCount, int evidencePerEvent)
    {
        var rawDir = Path.Combine(Path.GetDirectoryName(path)!, "raw");
        Directory.CreateDirectory(rawDir);

        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WritePropertyName("events");
        writer.WriteStartArray();
        var ts = DateTime.UtcNow;
        for (var e = 0; e < eventCount; e++)
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", ts.AddMinutes(e).ToString("O"));
            writer.WriteString("category", "File");
            writer.WriteString("action", "Open");
            writer.WritePropertyName("evidence");
            writer.WriteStartArray();
            for (var r = 0; r < evidencePerEvent; r++)
            {
                writer.WriteStartObject();
                writer.WriteString("source", "evtx");
                writer.WriteString("reference", $"raw/evtx/{e}_{r}.evtx");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
