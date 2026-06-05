using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Settings;
using WinDFIR.Core.Snapshot;
using Xunit;

namespace WinDFIR.Tests;

/// <summary>
/// Tests for snapshot export and manifest (e.g. machineSid stability).
/// </summary>
public class SnapshotExporterTests
{
    [Fact]
    public async Task ExportAsync_ThrowsOperationCanceled_WhenCancellationIsRequestedBeforeStart()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var index = new InMemoryActivityIndex(10);
        var exporter = new SnapshotExporter { UseVssSnapshots = false };
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_ExportCancelStart_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                exporter.ExportAsync(index, tempDir, null, cts.Token));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task ExportAsync_ThrowsOperationCanceled_WhenCancelledDuringArtifactCopy()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_ExportCancelArtifact_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var artifactDir = Path.Combine(tempDir, "artifacts");
            Directory.CreateDirectory(artifactDir);

            var index = new InMemoryActivityIndex(10);
            const int fileCount = 120;
            for (var i = 0; i < fileCount; i++)
            {
                var p = Path.Combine(artifactDir, $"sample{i}.lnk");
                await File.WriteAllTextAsync(p, new string('x', 8192));
                index.AddEvent(new ActivityEvent
                {
                    Timestamp = DateTime.UtcNow.AddTicks(i),
                    Category = "File",
                    Action = "Open",
                    Summary = "cancellation mid export",
                    Evidence = new List<EvidenceRef>
                    {
                        new("RecentLnk", p)
                    }
                });
            }

            var outDir = Path.Combine(tempDir, "out");
            Directory.CreateDirectory(outDir);
            using var cts = new CancellationTokenSource();
            var exporter = new SnapshotExporter { UseVssSnapshots = false };
            var exportTask = Task.Run(() => exporter.ExportAsync(index, outDir, null, cts.Token), CancellationToken.None);

            string? snapshotDir = null;
            for (var attempt = 0; attempt < 800 && snapshotDir == null; attempt++)
            {
                if (Directory.Exists(outDir))
                {
                    var dirs = Directory.GetDirectories(outDir);
                    if (dirs.Length > 0)
                        snapshotDir = dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).First();
                }

                await Task.Delay(5);
            }

            Assert.NotNull(snapshotDir);
            var rawDir = Path.Combine(snapshotDir, "raw");
            for (var attempt = 0; attempt < 600; attempt++)
            {
                if (Directory.Exists(rawDir))
                {
                    var n = Directory.GetFiles(rawDir, "*.lnk", SearchOption.AllDirectories).Length;
                    if (n >= 3)
                        break;
                }

                await Task.Delay(5);
            }

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exportTask);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task ExportAsync_HashesTxt_ContainsOneLinePerRawFile()
    {
        var index = new InMemoryActivityIndex(10);
        var exporter = new SnapshotExporter { UseVssSnapshots = false };
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_HashPerRaw_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var artifactPath = Path.Combine(tempDir, "sample.lnk");
            await File.WriteAllTextAsync(artifactPath, "demo");

            index.AddEvent(new ActivityEvent
            {
                Timestamp = DateTime.UtcNow,
                Category = "File",
                Action = "Open",
                Summary = "hash coverage",
                Evidence = new List<EvidenceRef>
                {
                    new("RecentLnk", artifactPath)
                }
            });

            await exporter.ExportAsync(index, tempDir);

            var snapshotDir = Directory.GetDirectories(tempDir)
                .Single(path => Path.GetFileName(path).StartsWith("snapshot_", StringComparison.OrdinalIgnoreCase));
            var rawRoot = Path.Combine(snapshotDir, "raw");
            var rawFileCount = Directory.GetFiles(rawRoot, "*", SearchOption.AllDirectories).Length;
            Assert.True(rawFileCount > 0);

            var hashesPath = Path.Combine(snapshotDir, "hashes.txt");
            var lines = await File.ReadAllLinesAsync(hashesPath);
            var rawHashLines = 0;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                var parts = trimmed.Split("  ", 2, StringSplitOptions.None);
                if (parts.Length != 2)
                    continue;

                var rel = parts[1].Trim().Replace('\\', '/');
                if (rel.StartsWith("raw/", StringComparison.OrdinalIgnoreCase))
                    rawHashLines++;
            }

            Assert.Equal(rawFileCount, rawHashLines);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task ExportAsync_WritesManifest_WithValidMachineSid()
    {
        var index = new InMemoryActivityIndex(10);
        var exporter = new SnapshotExporter { UseVssSnapshots = false };
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_SnapshotTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            await exporter.ExportAsync(index, tempDir);

            var snapshotDir = Directory.GetDirectories(tempDir).Single();
            var manifestPath = Path.Combine(snapshotDir, "manifest.json");
            Assert.True(File.Exists(manifestPath));

            var json = await File.ReadAllTextAsync(manifestPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var host = root.GetProperty("host");
            var machineSid = host.GetProperty("machineSid").GetString();

            Assert.NotNull(machineSid);
            Assert.True(
                machineSid.StartsWith("S-1-5-", StringComparison.Ordinal) ||
                machineSid.StartsWith("MachineGuid:", StringComparison.Ordinal) ||
                machineSid == "Unknown",
                $"machineSid should be S-1-5-*, MachineGuid:*, or Unknown; got: {machineSid}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task ExportAsync_Manifest_HasRequiredHostFields()
    {
        var index = new InMemoryActivityIndex(10);
        var exporter = new SnapshotExporter { UseVssSnapshots = false };
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_ManifestTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var options = new SnapshotExportOptions
            {
                ManifestExtras = new Dictionary<string, object?>
                {
                    ["toolVersion"] = "1.0.1-test"
                }
            };
            await exporter.ExportAsync(index, tempDir, options);

            var snapshotDir = Directory.GetDirectories(tempDir).Single();
            var manifestPath = Path.Combine(snapshotDir, "manifest.json");
            Assert.True(File.Exists(manifestPath));

            var json = await File.ReadAllTextAsync(manifestPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("collectionTime", out _));
            Assert.True(root.TryGetProperty("snapshotFormat", out _));
            Assert.True(root.TryGetProperty("toolVersion", out var toolVersionEl));
            Assert.Equal("1.0.1-test", toolVersionEl.GetString());
            var host = root.GetProperty("host");
            Assert.True(host.TryGetProperty("hostname", out _));
            Assert.True(host.TryGetProperty("machineSid", out var sidEl));
            var sid = sidEl.GetString();
            Assert.NotNull(sid);
            Assert.True(host.TryGetProperty("osVersion", out _));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task ExportAsync_RewritesCopiedEvidenceReferences_ToSnapshotLocalRawPaths()
    {
        var index = new InMemoryActivityIndex(10);
        var exporter = new SnapshotExporter { UseVssSnapshots = false };
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_SnapshotEvidence_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var artifactPath = Path.Combine(tempDir, "sample.lnk");
            await File.WriteAllTextAsync(artifactPath, "demo");

            index.AddEvent(new ActivityEvent
            {
                Timestamp = DateTime.UtcNow,
                Category = "File",
                Action = "Open",
                Summary = "Recent file evidence",
                Evidence = new List<EvidenceRef>
                {
                    new("RecentLnk", artifactPath)
                }
            });

            await exporter.ExportAsync(index, tempDir);

            var snapshotDir = Directory.GetDirectories(tempDir)
                .Single(path => Path.GetFileName(path).StartsWith("snapshot_", StringComparison.OrdinalIgnoreCase));
            var timelinePath = Path.Combine(snapshotDir, "timeline.json");
            var json = await File.ReadAllTextAsync(timelinePath);
            using var doc = JsonDocument.Parse(json);

            var rewrittenReference = doc.RootElement
                .GetProperty("events")[0]
                .GetProperty("evidence")[0]
                .GetProperty("reference")
                .GetString();

            Assert.NotNull(rewrittenReference);
            Assert.StartsWith("raw/lnk/", rewrittenReference!, StringComparison.OrdinalIgnoreCase);

            var copiedArtifactPath = Path.GetFullPath(Path.Combine(snapshotDir, rewrittenReference!.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(File.Exists(copiedArtifactPath));

            var loaded = SnapshotImporter.LoadFromFolder(snapshotDir);
            var loadedEvent = loaded.GetEventsByTimeRange(DateTime.MinValue, DateTime.MaxValue).Single();
            Assert.Single(loadedEvent.Evidence);
            Assert.Equal(copiedArtifactPath, loadedEvent.Evidence[0].Reference);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task ExportAsync_Manifest_TracksArtifactCopyCounts()
    {
        var index = new InMemoryActivityIndex(10);
        var exporter = new SnapshotExporter { UseVssSnapshots = false };
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_ArtifactSummary_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var artifactPath = Path.Combine(tempDir, "sample.pf");
            var missingPath = Path.Combine(tempDir, "missing.pf");
            await File.WriteAllTextAsync(artifactPath, "demo");

            index.AddEvent(new ActivityEvent
            {
                Timestamp = DateTime.UtcNow,
                Category = "File",
                Action = "Open",
                Summary = "Artifact summary test",
                Evidence = new List<EvidenceRef>
                {
                    new("Prefetch", artifactPath),
                    new("Prefetch", artifactPath),
                    new("Prefetch", missingPath)
                }
            });

            await exporter.ExportAsync(index, tempDir);

            var snapshotDir = Directory.GetDirectories(tempDir).Single();
            var manifestPath = Path.Combine(snapshotDir, "manifest.json");
            var json = await File.ReadAllTextAsync(manifestPath);
            using var doc = JsonDocument.Parse(json);
            var summary = doc.RootElement.GetProperty("collectionSummary");

            Assert.Equal(3, summary.GetProperty("evidenceReferenceCount").GetInt32());
            Assert.Equal(2, summary.GetProperty("rewrittenEvidenceReferenceCount").GetInt32());
            Assert.Equal(1, summary.GetProperty("copiedArtifactFileCount").GetInt32());
            Assert.Equal(1, summary.GetProperty("skippedEvidenceReferenceCount").GetInt32());
            Assert.Equal(0, summary.GetProperty("failedEvidenceReferenceCount").GetInt32());
            Assert.True(summary.GetProperty("wasArtifactCopyIncomplete").GetBoolean());
            Assert.False(summary.GetProperty("usedVssSnapshotForArtifactCopy").GetBoolean());
            Assert.Equal(0, summary.GetProperty("artifactCopyWarningCount").GetInt32());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task ExportAsync_Manifest_DoesNotReportArtifactCopyWarning_WhenNoEvidencePaths()
    {
        var index = new InMemoryActivityIndex(10);
        index.AddEvent(new ActivityEvent
        {
            Timestamp = DateTime.UtcNow,
            Category = "Process",
            Action = "Start",
            Summary = "No artifact evidence",
            Evidence = new List<EvidenceRef>()
        });

        var exporter = new SnapshotExporter { UseVssSnapshots = true };
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_NoArtifactWarning_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var options = new SnapshotExportOptions
            {
                ManifestExtras = new Dictionary<string, object?>
                {
                    ["toolVersion"] = "1.0.1-test"
                }
            };
            await exporter.ExportAsync(index, tempDir, options);

            var snapshotDir = Directory.GetDirectories(tempDir).Single();
            var manifestPath = Path.Combine(snapshotDir, "manifest.json");
            var json = await File.ReadAllTextAsync(manifestPath);
            using var doc = JsonDocument.Parse(json);
            var summary = doc.RootElement.GetProperty("collectionSummary");

            Assert.Equal(0, summary.GetProperty("artifactCopyWarningCount").GetInt32());
            Assert.False(summary.GetProperty("usedVssSnapshotForArtifactCopy").GetBoolean());
            Assert.False(summary.TryGetProperty("artifactCopyWarning", out _));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task ExportAsync_Manifest_IncludesCollectionMetadataExtras()
    {
        var index = new InMemoryActivityIndex(10);
        index.AddEvent(new ActivityEvent
        {
            Timestamp = new DateTime(2026, 3, 19, 4, 0, 0, DateTimeKind.Utc),
            Category = "Process",
            Action = "Start",
            Summary = "Demo event",
            Evidence = new List<EvidenceRef>()
        });

        var exporter = new SnapshotExporter { UseVssSnapshots = false };
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_ManifestMetadata_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var settings = new HostWitnessSettings
            {
                Ui = new UiSettings
                {
                    RegistryUseOfflineOnly = false,
                    EnableLiveRegistryExperimental = true,
                    TimeZoneDisplay = "UTC"
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

            var manifestExtras = CollectionMetadataBuilder.BuildBaseManifestExtras(
                settings,
                executionContext: "agent_headless",
                useVssSnapshots: false,
                enabledProviders: new[] { "EventLogProvider", "NetConnectionProvider" },
                collectSeconds: 30,
                isAdministrator: true,
                isVssServiceRunning: true,
                generatedAtUtc: new DateTime(2026, 3, 19, 4, 5, 6, DateTimeKind.Utc));
            manifestExtras["toolVersion"] = "1.0.1-test";

            var options = new SnapshotExportOptions
            {
                ManifestExtras = manifestExtras,
                SourceEventCount = index.EventCount,
                CollectionSummaryExtras = new Dictionary<string, object?>
                {
                    ["preflightWarningCount"] = 2,
                    ["preflightErrorCount"] = 0,
                    ["etwDroppedEventTotal"] = 7L,
                    ["uiBackpressureDroppedTotal"] = 3L
                }
            };

            await exporter.ExportAsync(index, tempDir, options);

            var snapshotDir = Directory.GetDirectories(tempDir).Single();
            var manifestPath = Path.Combine(snapshotDir, "manifest.json");
            Assert.True(File.Exists(manifestPath));

            var json = await File.ReadAllTextAsync(manifestPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("1.0.1-test", root.GetProperty("toolVersion").GetString());
            Assert.Equal("triage_fast", root.GetProperty("modeProfile").GetString());
            Assert.Equal("live_non_forensic", root.GetProperty("registryMode").GetString());
            Assert.True(root.GetProperty("registryLiveEnabled").GetBoolean());

            var preflight = root.GetProperty("preflight");
            Assert.Equal("agent_headless", preflight.GetProperty("executionContext").GetString());
            Assert.Equal("triage_fast", preflight.GetProperty("modeProfile").GetString());
            Assert.Equal("2026-03-19T04:05:06.0000000Z", preflight.GetProperty("generatedAtUtc").GetString());
            Assert.Equal("UTC", preflight.GetProperty("timeZoneDisplay").GetString());
            Assert.Equal(30, preflight.GetProperty("collectSeconds").GetInt32());

            var enabledProviders = preflight.GetProperty("enabledProviders").EnumerateArray().Select(p => p.GetString()).ToArray();
            Assert.Equal(new[] { "EventLogProvider", "NetConnectionProvider" }, enabledProviders);

            var summary = root.GetProperty("collectionSummary");
            Assert.Equal(1, summary.GetProperty("sourceEventCount").GetInt64());
            Assert.Equal(1, summary.GetProperty("exportedEventCount").GetInt32());
            Assert.Equal(SnapshotExporter.ExportMaxEvents, summary.GetProperty("eventCap").GetInt32());
            Assert.False(summary.GetProperty("wasEventCountCapped").GetBoolean());
            Assert.Equal(0, summary.GetProperty("evidenceReferenceCount").GetInt32());
            Assert.Equal(0, summary.GetProperty("rewrittenEvidenceReferenceCount").GetInt32());
            Assert.Equal(0, summary.GetProperty("copiedArtifactFileCount").GetInt32());
            Assert.Equal(0, summary.GetProperty("skippedEvidenceReferenceCount").GetInt32());
            Assert.Equal(0, summary.GetProperty("failedEvidenceReferenceCount").GetInt32());
            Assert.False(summary.GetProperty("wasArtifactCopyIncomplete").GetBoolean());
            Assert.False(summary.GetProperty("usedVssSnapshotForArtifactCopy").GetBoolean());
            Assert.Equal(0, summary.GetProperty("artifactCopyWarningCount").GetInt32());
            Assert.Equal(2, summary.GetProperty("preflightWarningCount").GetInt32());
            Assert.Equal(0, summary.GetProperty("preflightErrorCount").GetInt32());
            Assert.Equal(7L, summary.GetProperty("etwDroppedEventTotal").GetInt64());
            Assert.Equal(3L, summary.GetProperty("uiBackpressureDroppedTotal").GetInt64());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
            }
        }
    }
}
