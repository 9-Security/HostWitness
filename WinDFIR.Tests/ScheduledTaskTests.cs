using System.Text.Json;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Snapshot;
using WinDFIR.Providers;
using WinDFIR.Providers.Parsers;
using Xunit;

namespace WinDFIR.Tests;

public class ScheduledTaskTests
{
    private const string FullTaskXml = """
    <?xml version="1.0" encoding="UTF-16"?>
    <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
      <RegistrationInfo>
        <Date>2026-01-15T08:00:00</Date>
        <Author>EVIL\admin</Author>
        <Description>Test persistence task</Description>
        <URI>\Custom\EvilTask</URI>
      </RegistrationInfo>
      <Triggers>
        <LogonTrigger>
          <Enabled>true</Enabled>
        </LogonTrigger>
        <CalendarTrigger>
          <StartBoundary>2026-01-16T09:00:00</StartBoundary>
          <Enabled>false</Enabled>
        </CalendarTrigger>
      </Triggers>
      <Principals>
        <Principal id="Author">
          <UserId>S-1-5-18</UserId>
          <RunLevel>HighestAvailable</RunLevel>
          <LogonType>Password</LogonType>
        </Principal>
      </Principals>
      <Settings>
        <Enabled>true</Enabled>
        <Hidden>true</Hidden>
      </Settings>
      <Actions Context="Author">
        <Exec>
          <Command>C:\Windows\System32\cmd.exe</Command>
          <Arguments>/c evil.bat</Arguments>
          <WorkingDirectory>C:\temp</WorkingDirectory>
        </Exec>
      </Actions>
    </Task>
    """;

    [Fact]
    public void ParseXml_ReadsNamespacedTaskFully()
    {
        var record = ScheduledTaskParser.ParseXml(FullTaskXml, "\\Custom\\EvilTask", @"C:\Windows\System32\Tasks\Custom\EvilTask");

        Assert.NotNull(record);
        Assert.Equal("\\Custom\\EvilTask", record!.TaskName);
        Assert.Equal("EVIL\\admin", record.Author);
        Assert.Equal("Test persistence task", record.Description);
        Assert.Equal("\\Custom\\EvilTask", record.Uri);
        Assert.Equal("S-1-5-18", record.PrincipalUserId);
        Assert.Equal("HighestAvailable", record.RunLevel);
        Assert.Equal("Password", record.LogonType);
        Assert.True(record.Enabled);
        Assert.True(record.Hidden);

        Assert.Equal(2, record.Triggers.Count);
        Assert.Contains(record.Triggers, t => t.Type == "LogonTrigger" && t.Enabled == true);
        Assert.Contains(record.Triggers, t => t.Type == "CalendarTrigger" && t.Enabled == false && t.StartBoundaryUtc.HasValue);

        var exec = record.PrimaryExec;
        Assert.NotNull(exec);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", exec!.Command);
        Assert.Equal("/c evil.bat", exec.Arguments);
        Assert.Equal(@"C:\temp", exec.WorkingDirectory);

        Assert.NotNull(record.RegistrationDateUtc);
    }

    [Fact]
    public void ParseXml_ReadsComHandlerAction()
    {
        const string xml = """
        <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Actions>
            <ComHandler>
              <ClassId>{11111111-2222-3333-4444-555555555555}</ClassId>
            </ComHandler>
          </Actions>
        </Task>
        """;

        var record = ScheduledTaskParser.ParseXml(xml, "\\Com");
        Assert.NotNull(record);
        var action = Assert.Single(record!.Actions);
        Assert.Equal("ComHandler", action.Type);
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", action.ComClassId);
        Assert.Null(record.PrimaryExec);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml <<<")]
    [InlineData("<NotATask><x/></NotATask>")]
    public void ParseXml_ReturnsNull_OnMalformedOrWrongRoot(string xml)
    {
        Assert.Null(ScheduledTaskParser.ParseXml(xml, "\\x"));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\Tasks\Microsoft\Windows\Defrag\ScheduledDefrag", "\\Microsoft\\Windows\\Defrag\\ScheduledDefrag")]
    [InlineData(@"C:\Windows\System32\Tasks\EvilTask", "\\EvilTask")]
    public void DeriveTaskNameFromPath_BuildsCanonicalName(string path, string expected)
    {
        Assert.Equal(expected, ScheduledTaskParser.DeriveTaskNameFromPath(path));
    }

    [Fact]
    public async Task Provider_EmitsPersistenceEvents_FromTasksRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "HostWitness_TasksRoot_" + Guid.NewGuid().ToString("N"));
        var subDir = Path.Combine(root, "Custom");
        Directory.CreateDirectory(subDir);
        try
        {
            // Extensionless and UTF-16 (with BOM), like real System32\Tasks entries.
            await File.WriteAllTextAsync(Path.Combine(subDir, "EvilTask"), FullTaskXml, System.Text.Encoding.Unicode);
            await File.WriteAllTextAsync(Path.Combine(root, "NotATask"), "garbage, not xml");

            var events = new List<ActivityEvent>();
            var provider = new ScheduledTaskProvider(root);
            provider.EventProduced += (_, e) =>
            {
                lock (events) events.Add(e);
            };

            await provider.StartAsync();
            await WaitForCountAsync(events, 1, TimeSpan.FromSeconds(5));
            await provider.StopAsync();

            var evt = Assert.Single(events);
            Assert.Equal("Persistence", evt.Category);
            Assert.Equal("ScheduledTask", evt.Action);
            Assert.Equal("\\Custom\\EvilTask", evt.Fields["TaskName"]);
            Assert.Equal(@"C:\Windows\System32\cmd.exe", evt.Fields["ExecCommand"]);
            Assert.True((bool)evt.Fields["Hidden"]);
            Assert.NotNull(evt.ObjectFile);
            var evidence = Assert.Single(evt.Evidence);
            Assert.Equal("ScheduledTask", evidence.Source);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Exporter_CopiesScheduledTaskArtifact_NotSkipped()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "HostWitness_TaskExport_" + Guid.NewGuid().ToString("N"));
        var artifactDir = Path.Combine(tempDir, "Tasks", "Custom");
        Directory.CreateDirectory(artifactDir);
        try
        {
            var taskFile = Path.Combine(artifactDir, "EvilTask"); // extensionless, UTF-16 like real files
            await File.WriteAllTextAsync(taskFile, FullTaskXml, System.Text.Encoding.Unicode);

            var index = new InMemoryActivityIndex(10);
            index.AddEvent(new ActivityEvent
            {
                Timestamp = DateTime.UtcNow,
                Category = "Persistence",
                Action = "ScheduledTask",
                Summary = "task",
                Evidence = new List<EvidenceRef> { new("ScheduledTask", taskFile) }
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
            Assert.Equal(0, summary.GetProperty("failedEvidenceReferenceCount").GetInt32());

            var tasksRaw = Path.Combine(snapshotDir, "raw", "tasks");
            Assert.True(Directory.Exists(tasksRaw));
            Assert.NotEmpty(Directory.GetFiles(tasksRaw));
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
