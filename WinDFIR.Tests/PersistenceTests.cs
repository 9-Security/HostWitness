using System.Linq;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Snapshot;
using WinDFIR.Core.Settings;
using Xunit;

namespace WinDFIR.Tests;

/// <summary>
/// Tests for SessionPersistence, LayoutPersistence, and SqliteIndexPersistence (save/restore and export/load).
/// </summary>
public class SessionPersistenceTests
{
    [Fact]
    public void Save_ThenGetSavedSessionInfo_ReturnsCorrectCountAndSavedAt()
    {
        var index = new InMemoryActivityIndex(100);
        index.AddEvent(new ActivityEvent
        {
            Timestamp = DateTime.UtcNow,
            Category = "Test",
            Action = "Query",
            Summary = "Session test",
            Evidence = new List<EvidenceRef>()
        });
        index.AddEvent(new ActivityEvent
        {
            Timestamp = DateTime.UtcNow.AddSeconds(-1),
            Category = "File",
            Action = "Read",
            Summary = "File test",
            Evidence = new List<EvidenceRef>()
        });

        var folder = Path.Combine(Path.GetTempPath(), "HostWitness_SessionTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);
            var count = SessionPersistence.Save(index, folder);
            Assert.Equal(2, count);

            var info = SessionPersistence.GetSavedSessionInfo(folder);
            Assert.Equal(2, info.EventCount);
            Assert.NotNull(info.SavedAt);
            Assert.Equal(SessionPersistence.CurrentSessionSchemaVersion, info.SessionSchemaVersion);
            Assert.True(DateTime.TryParse(info.SavedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out _));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                try { Directory.Delete(folder, true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public void Save_ThenLoadEvents_RestoresSameEventCountAndKeyFields()
    {
        var index = new InMemoryActivityIndex(100);
        var ts = DateTime.UtcNow;
        index.AddEvent(new ActivityEvent
        {
            Timestamp = ts,
            Category = "Registry",
            Action = "Query",
            Summary = "Key value",
            Fields = new Dictionary<string, object> { { "ValueName", "Test" } },
            Evidence = new List<EvidenceRef>()
        });

        var folder = Path.Combine(Path.GetTempPath(), "HostWitness_SessionLoad_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);
            SessionPersistence.Save(index, folder);
            var loaded = SessionPersistence.LoadEvents(folder);
            Assert.Single(loaded);
            Assert.Equal(ts.ToString("O"), loaded[0].Timestamp.ToString("O"));
            Assert.Equal("Registry", loaded[0].Category);
            Assert.Equal("Query", loaded[0].Action);
            Assert.Equal("Key value", loaded[0].Summary);
            Assert.True(loaded[0].Fields?.ContainsKey("ValueName") == true);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                try { Directory.Delete(folder, true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public void TryLoadSession_SchemaTooNew_ReturnsUnsupported()
    {
        var folder = Path.Combine(Path.GetTempPath(), "HostWitness_SessionSchema_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, SessionPersistence.MetaFileName),
                """{"sessionSchemaVersion":99,"eventCount":1,"savedAt":"2020-01-01T00:00:00.0000000Z"}""");
            File.WriteAllText(
                Path.Combine(folder, SessionPersistence.TimelineFileName),
                """{"events":[{"timestamp":"2020-01-01T00:00:00.0000000Z","category":"Test","action":"A","summary":"x","evidence":[]}]}""");

            var r = SessionPersistence.TryLoadSession(folder);
            Assert.False(r.Success);
            Assert.Equal(SessionLoadFailureKind.UnsupportedSchemaVersion, r.Failure);
            Assert.Equal(99, r.SessionSchemaVersion);
            Assert.Empty(SessionPersistence.LoadEvents(folder));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                try { Directory.Delete(folder, true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public void TryLoadSession_LegacyMetaWithoutSchemaVersion_StillLoads()
    {
        var folder = Path.Combine(Path.GetTempPath(), "HostWitness_SessionLegacy_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, SessionPersistence.MetaFileName),
                """{"eventCount":1,"savedAt":"2020-01-01T00:00:00.0000000Z"}""");
            File.WriteAllText(
                Path.Combine(folder, SessionPersistence.TimelineFileName),
                """{"events":[{"timestamp":"2020-01-01T00:00:00.0000000Z","category":"Legacy","action":"A","summary":"ok","evidence":[]}]}""");

            var info = SessionPersistence.GetSavedSessionInfo(folder);
            Assert.Equal(0, info.SessionSchemaVersion);

            var r = SessionPersistence.TryLoadSession(folder);
            Assert.True(r.Success);
            Assert.Single(r.Events);
            Assert.Equal("Legacy", r.Events[0].Category);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                try { Directory.Delete(folder, true); } catch { /* ignore */ }
            }
        }
    }
}

public class LayoutPersistenceTests
{
    [Fact]
    public void Save_ThenLoadState_ReturnsSameValues()
    {
        var tempAppData = Path.Combine(Path.GetTempPath(), "HostWitness_LayoutTest_" + Guid.NewGuid().ToString("N")[..8]);
        var origAppData = Environment.GetEnvironmentVariable("APPDATA");
        try
        {
            Directory.CreateDirectory(tempAppData);
            Environment.SetEnvironmentVariable("APPDATA", tempAppData, EnvironmentVariableTarget.Process);
            var left = 100.0;
            var top = 50.0;
            var width = 800.0;
            var height = 600.0;
            var dynIdx = 1;
            var staticIdx = 2;
            LayoutPersistence.Save(left, top, width, height, dynIdx, staticIdx, null, null);
            var state = LayoutPersistence.LoadState();
            Assert.NotNull(state);
            Assert.Equal(left, state.Left);
            Assert.Equal(top, state.Top);
            Assert.Equal(width, state.Width);
            Assert.Equal(height, state.Height);
            Assert.Equal(dynIdx, state.SelectedDynamicTabIndex);
            Assert.Equal(staticIdx, state.SelectedStaticTabIndex);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", origAppData, EnvironmentVariableTarget.Process);
            if (Directory.Exists(tempAppData))
            {
                try { Directory.Delete(tempAppData, true); } catch { /* ignore */ }
            }
        }
    }

}

public class SqliteIndexPersistenceTests
{
    [Fact]
    public void Export_ThenLoadEvents_ReturnsSameCountAndKeyFields()
    {
        var index = new InMemoryActivityIndex(100);
        index.AddEvent(new ActivityEvent
        {
            Timestamp = DateTime.UtcNow,
            Category = "File",
            Action = "Create",
            Summary = "test.txt",
            Evidence = new List<EvidenceRef>()
        });
        index.AddEvent(new ActivityEvent
        {
            Timestamp = DateTime.UtcNow.AddMinutes(-1),
            Category = "Registry",
            Action = "SetValue",
            Summary = "Run key",
            Evidence = new List<EvidenceRef>()
        });

        var dbPath = Path.Combine(Path.GetTempPath(), "HostWitness_SqliteTest_" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            var count = SqliteIndexPersistence.Export(index, dbPath);
            Assert.Equal(2, count);
            Assert.True(File.Exists(dbPath));

            var loaded = SqliteIndexPersistence.LoadEvents(dbPath);
            Assert.Equal(2, loaded.Count);
            var categories = loaded.Select(e => e.Category).OrderBy(c => c).ToList();
            Assert.Contains("File", categories);
            Assert.Contains("Registry", categories);
            Assert.Contains(loaded, e => e.Summary == "test.txt");
            Assert.Contains(loaded, e => e.Summary == "Run key");
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public void Export_ThenLoadEventsPage_ReturnsCorrectPage()
    {
        var index = new InMemoryActivityIndex(100);
        for (int i = 0; i < 5; i++)
        {
            index.AddEvent(new ActivityEvent
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-i),
                Category = "Test",
                Action = "Query",
                Summary = $"Event {i}",
                Evidence = new List<EvidenceRef>()
            });
        }

        var dbPath = Path.Combine(Path.GetTempPath(), "HostWitness_SqlitePage_" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            SqliteIndexPersistence.Export(index, dbPath);
            var page1 = SqliteIndexPersistence.LoadEventsPage(dbPath, DateTime.MinValue, DateTime.MaxValue, 0, 2);
            Assert.Equal(2, page1.Count);
            var page2 = SqliteIndexPersistence.LoadEventsPage(dbPath, DateTime.MinValue, DateTime.MaxValue, 2, 2);
            Assert.Equal(2, page2.Count);
            var page3 = SqliteIndexPersistence.LoadEventsPage(dbPath, DateTime.MinValue, DateTime.MaxValue, 4, 10);
            Assert.Single(page3);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { /* ignore */ }
            }
        }
    }
}
