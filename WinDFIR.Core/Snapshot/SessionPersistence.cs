using System.Text.Json;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;

namespace WinDFIR.Core.Snapshot;

/// <summary>Summary from <see cref="SessionPersistence.MetaFileName"/> for restore prompts.</summary>
public readonly record struct SessionSavedInfo(int EventCount, string? SavedAt, int SessionSchemaVersion);

/// <summary>Outcome of <see cref="SessionPersistence.TryLoadSession"/>.</summary>
public readonly record struct SessionLoadResult(
    bool Success,
    IReadOnlyList<ActivityEvent> Events,
    SessionLoadFailureKind? Failure,
    int SessionSchemaVersion);

public enum SessionLoadFailureKind
{
    UnsupportedSchemaVersion,
    MissingOrEmptyTimeline,
    TimelineParseError,
}

/// <summary>
/// Saves and restores the activity index to/from a session folder (e.g. last_session).
/// Used for "Save session on exit" and "Restore previous session" on startup.
/// </summary>
public static class SessionPersistence
{
    /// <summary>Maximum events to save in one session to avoid excessive memory and file size.</summary>
    public const int SaveMaxEvents = 500_000;

    /// <summary>Format version written to <see cref="MetaFileName"/>; increment when timeline/meta shape changes.</summary>
    public const int CurrentSessionSchemaVersion = 1;

    public const string MetaFileName = "meta.json";
    public const string TimelineFileName = "timeline.json";

    /// <summary>Default folder for last session: %AppData%\HostWitness\last_session.</summary>
    public static string GetDefaultSessionFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "HostWitness", "last_session");
    }

    /// <summary>
    /// Removes timeline.json and meta.json under <paramref name="folderPath"/> so a later run does not
    /// restore a stale session after the user closed with an empty timeline.
    /// </summary>
    public static void ClearSavedSession(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath))
                return;
            var timelinePath = Path.Combine(folderPath, TimelineFileName);
            var metaPath = Path.Combine(folderPath, MetaFileName);
            if (File.Exists(timelinePath))
                File.Delete(timelinePath);
            if (File.Exists(metaPath))
                File.Delete(metaPath);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Saves index events to the given folder (timeline.json + meta.json). Returns saved event count.</summary>
    public static int Save(IActivityIndex index, string folderPath)
    {
        var events = index.GetEventsByTimeRange(DateTime.MinValue, DateTime.MaxValue).Take(SaveMaxEvents).ToList();
        Directory.CreateDirectory(folderPath);

        var timeline = new
        {
            events = events.Select(e => new
            {
                timestamp = e.Timestamp.ToString("O"),
                category = e.Category,
                action = e.Action,
                subjectProcess = e.SubjectProcess?.ToString(),
                subjectUser = e.SubjectUser?.ToString(),
                objectFile = e.ObjectFile?.ToString(),
                objectRegistry = e.ObjectRegistry?.ToString(),
                objectNetworkFlow = e.ObjectNetworkFlow?.ToString(),
                objectUrl = e.ObjectUrl,
                summary = e.Summary,
                fields = e.Fields,
                evidence = e.Evidence.Select(ev => new
                {
                    source = ev.Source,
                    reference = ev.Reference,
                    hash = ev.Hash,
                    collectedAt = ev.CollectedAt?.ToString("O")
                }),
                confidence = e.Confidence
            })
        };
        var timelinePath = Path.Combine(folderPath, TimelineFileName);
        File.WriteAllText(timelinePath, JsonSerializer.Serialize(timeline, new JsonSerializerOptions { WriteIndented = true }));

        var meta = new
        {
            sessionSchemaVersion = CurrentSessionSchemaVersion,
            eventCount = events.Count,
            savedAt = DateTime.UtcNow.ToString("O")
        };
        var metaPath = Path.Combine(folderPath, MetaFileName);
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

        return events.Count;
    }

    /// <summary>
    /// Saves index events asynchronously to the given folder (timeline.json + meta.json).
    /// Offloads serialization and file writes to a background thread.
    /// </summary>
    public static Task<int> SaveAsync(IActivityIndex index, string folderPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Save(index, folderPath);
        }, cancellationToken);
    }

    /// <summary>Reads meta.json if present; otherwise EventCount 0. Legacy files without <c>sessionSchemaVersion</c> use version 0.</summary>
    public static SessionSavedInfo GetSavedSessionInfo(string folderPath)
    {
        var metaPath = Path.Combine(folderPath, MetaFileName);
        if (!File.Exists(metaPath))
            return new SessionSavedInfo(0, null, 0);
        try
        {
            var json = File.ReadAllText(metaPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var count = root.TryGetProperty("eventCount", out var c) ? c.GetInt32() : 0;
            var savedAt = root.TryGetProperty("savedAt", out var s) ? s.GetString() : null;
            var schema = 0;
            if (root.TryGetProperty("sessionSchemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number)
                schema = sv.GetInt32();
            return new SessionSavedInfo(count, savedAt, schema);
        }
        catch
        {
            return new SessionSavedInfo(0, null, 0);
        }
    }

    /// <summary>
    /// Loads session events with explicit success/failure. Enforces <see cref="CurrentSessionSchemaVersion"/> when meta declares a newer schema.
    /// Legacy folders (no meta or no <c>sessionSchemaVersion</c>) load via <see cref="SnapshotImporter"/> if timeline exists.
    /// </summary>
    public static SessionLoadResult TryLoadSession(string folderPath)
    {
        SessionSavedInfo meta;
        try
        {
            meta = GetSavedSessionInfo(folderPath);
        }
        catch
        {
            meta = new SessionSavedInfo(0, null, 0);
        }

        if (meta.SessionSchemaVersion > CurrentSessionSchemaVersion)
        {
            return new SessionLoadResult(false, Array.Empty<ActivityEvent>(), SessionLoadFailureKind.UnsupportedSchemaVersion, meta.SessionSchemaVersion);
        }

        try
        {
            var snapshot = SnapshotImporter.LoadFromFolder(folderPath);
            var events = snapshot.GetEventsByTimeRange(DateTime.MinValue, DateTime.MaxValue).ToList();

            if (meta.EventCount > 0 && events.Count == 0)
            {
                return new SessionLoadResult(false, Array.Empty<ActivityEvent>(), SessionLoadFailureKind.MissingOrEmptyTimeline, meta.SessionSchemaVersion);
            }

            return new SessionLoadResult(true, events, null, meta.SessionSchemaVersion);
        }
        catch (Exception)
        {
            return new SessionLoadResult(false, Array.Empty<ActivityEvent>(), SessionLoadFailureKind.TimelineParseError, meta.SessionSchemaVersion);
        }
    }

    /// <summary>Loads events from the session folder (timeline.json). Returns empty list if not found, invalid, or unsupported schema.</summary>
    public static List<ActivityEvent> LoadEvents(string folderPath)
    {
        var r = TryLoadSession(folderPath);
        return r.Success ? r.Events.ToList() : new List<ActivityEvent>();
    }

    /// <summary>Serializes one event to JSON (same format as timeline.json events array element) for SQLite or other storage.</summary>
    public static string SerializeEventToJson(ActivityEvent e)
    {
        var obj = new
        {
            timestamp = e.Timestamp.ToString("O"),
            category = e.Category,
            action = e.Action,
            subjectProcess = e.SubjectProcess?.ToString(),
            subjectUser = e.SubjectUser?.ToString(),
            objectFile = e.ObjectFile?.ToString(),
            objectRegistry = e.ObjectRegistry?.ToString(),
            objectNetworkFlow = e.ObjectNetworkFlow?.ToString(),
            objectUrl = e.ObjectUrl,
            summary = e.Summary,
            fields = e.Fields,
            evidence = e.Evidence.Select(ev => new { source = ev.Source, reference = ev.Reference, hash = ev.Hash, collectedAt = ev.CollectedAt?.ToString("O") }),
            confidence = e.Confidence
        };
        return JsonSerializer.Serialize(obj);
    }
}
