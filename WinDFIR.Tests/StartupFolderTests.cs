using System.Text.Json;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Snapshot;
using WinDFIR.Providers;
using Xunit;

namespace WinDFIR.Tests;

public class StartupFolderTests
{
    [Theory]
    [InlineData(@"C:\Users\victim\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup", "victim")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp", "AllUsers")]
    public void DeriveUser_FromFolderPath(string path, string expected)
    {
        Assert.Equal(expected, StartupFolderProvider.DeriveUser(path));
    }

    [Fact]
    public async Task Provider_EmitsPersistenceEvent_PerEntry_SkippingDesktopIni()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "HostWitness_Startup_" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(rootDir,
            "Users", "victim", "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "evil.bat"), "@echo off\r\nstart payload.exe");
            await File.WriteAllTextAsync(Path.Combine(folder, "desktop.ini"), "[.ShellClassInfo]");

            var events = new List<ActivityEvent>();
            var provider = new StartupFolderProvider(new[] { folder });
            provider.EventProduced += (_, e) =>
            {
                lock (events) events.Add(e);
            };

            await provider.StartAsync();
            await WaitForCountAsync(events, 1, TimeSpan.FromSeconds(5));
            await provider.StopAsync();

            var evt = Assert.Single(events); // desktop.ini skipped
            Assert.Equal("Persistence", evt.Category);
            Assert.Equal("StartupFolder", evt.Action);
            Assert.Equal("evil.bat", evt.Fields["EntryName"]);
            Assert.Equal("victim", evt.Fields["User"]);
            Assert.NotNull(evt.ObjectFile); // .bat is rooted
            Assert.Equal("StartupFolder", Assert.Single(evt.Evidence).Source);
        }
        finally
        {
            try { Directory.Delete(rootDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Exporter_CopiesStartupArtifact_NotSkipped()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_StartupExport_" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(srcDir);
        try
        {
            var entry = Path.Combine(srcDir, "evil.bat");
            await File.WriteAllTextAsync(entry, "start payload.exe");

            var index = new InMemoryActivityIndex(10);
            index.AddEvent(new ActivityEvent
            {
                Timestamp = DateTime.UtcNow,
                Category = "Persistence",
                Action = "StartupFolder",
                Summary = "startup",
                Evidence = new List<EvidenceRef> { new("StartupFolder", entry) }
            });

            var outDir = Path.Combine(tempDir, "out");
            Directory.CreateDirectory(outDir);
            var exporter = new SnapshotExporter { UseVssSnapshots = false };
            await exporter.ExportAsync(index, outDir);

            var snapshotDir = Directory.GetDirectories(outDir, "snapshot_*").Single();
            var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshotDir, "manifest.json")));
            var summary = manifest.RootElement.GetProperty("collectionSummary");
            Assert.Equal(1, summary.GetProperty("copiedArtifactFileCount").GetInt32());
            Assert.Equal(0, summary.GetProperty("skippedEvidenceReferenceCount").GetInt32());
            Assert.True(Directory.Exists(Path.Combine(snapshotDir, "raw", "startup")));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private static async Task WaitForCountAsync(List<ActivityEvent> events, int target, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            lock (events)
            {
                if (events.Count >= target)
                    return;
            }
            await Task.Delay(20);
        }
    }
}
