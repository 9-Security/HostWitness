using System.Globalization;
using System.Text.Json;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;

namespace WinDFIR.Core.Snapshot;

/// <summary>
/// Imports a snapshot folder (timeline.json) into a read-only index for offline viewing.
/// </summary>
public static class SnapshotImporter
{
    /// <summary>
    /// Loads timeline events from a snapshot directory. Looks for timeline.json in the given path
    /// or in the first snapshot_* subdirectory.
    /// </summary>
    /// <param name="folderPath">Path to the snapshot folder or its parent (if multiple <c>snapshot_*</c> children exist, the lexicographically smallest with <c>timeline.json</c> is used).</param>
    /// <param name="cancellationToken">Optional cancellation.</param>
    /// <returns>Read-only index with loaded events; empty if not found or invalid.</returns>
    public static async Task<SnapshotActivityIndex> LoadFromFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        var timelinePath = ResolveTimelinePath(folderPath);
        if (string.IsNullOrEmpty(timelinePath) || !File.Exists(timelinePath))
            return new SnapshotActivityIndex(Array.Empty<ActivityEvent>());

        var evidenceBaseDirectory = Path.GetDirectoryName(timelinePath);
        await using var fs = File.OpenRead(timelinePath);
        using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: cancellationToken);
        var events = LoadEvents(doc, evidenceBaseDirectory);

        return new SnapshotActivityIndex(events);
    }

    /// <summary>
    /// Synchronous overload: loads timeline from folder.
    /// </summary>
    public static SnapshotActivityIndex LoadFromFolder(string folderPath)
    {
        var timelinePath = ResolveTimelinePath(folderPath);
        if (string.IsNullOrEmpty(timelinePath) || !File.Exists(timelinePath))
            return new SnapshotActivityIndex(Array.Empty<ActivityEvent>());

        var evidenceBaseDirectory = Path.GetDirectoryName(timelinePath);
        using var fs = File.OpenRead(timelinePath);
        using var doc = JsonDocument.Parse(fs);
        var events = LoadEvents(doc, evidenceBaseDirectory);

        return new SnapshotActivityIndex(events);
    }

    private static List<ActivityEvent> LoadEvents(JsonDocument doc, string? evidenceBaseDirectory)
    {
        var events = new List<ActivityEvent>();
        var evidencePaths = CreateEvidencePaths(evidenceBaseDirectory);

        if (doc.RootElement.TryGetProperty("events", out var arr))
        {
            foreach (var ev in arr.EnumerateArray())
            {
                if (TryParseEvent(ev, evidencePaths, out var activityEvent))
                    events.Add(activityEvent);
            }
        }

        return events;
    }

    /// <summary>Resolved snapshot directory and normalized raw/ prefix for containment checks (computed once per timeline load).</summary>
    private readonly struct EvidencePaths
    {
        public EvidencePaths(string resolvedBaseDirectory, string rawDirectoryWithTrailingSeparator)
        {
            ResolvedBaseDirectory = resolvedBaseDirectory;
            RawDirectoryWithTrailingSeparator = rawDirectoryWithTrailingSeparator;
        }

        public string ResolvedBaseDirectory { get; }
        public string RawDirectoryWithTrailingSeparator { get; }
    }

    private static EvidencePaths? CreateEvidencePaths(string? evidenceBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(evidenceBaseDirectory))
            return null;

        var resolved = Path.GetFullPath(evidenceBaseDirectory);
        var rawRoot = EnsureTrailingDirectorySeparator(Path.GetFullPath(Path.Combine(resolved, "raw")));
        return new EvidencePaths(resolved, rawRoot);
    }

    private static string? ResolveTimelinePath(string folderPath)
    {
        var dir = Path.GetFullPath(folderPath);
        var direct = Path.Combine(dir, "timeline.json");
        if (File.Exists(direct))
            return direct;

        try
        {
            string? chosen = null;
            foreach (var sub in Directory.EnumerateDirectories(dir, "snapshot_*"))
            {
                var inSub = Path.Combine(sub, "timeline.json");
                if (!File.Exists(inSub))
                    continue;
                if (chosen == null || string.Compare(sub, chosen, StringComparison.OrdinalIgnoreCase) < 0)
                    chosen = sub;
            }

            if (chosen != null)
                return Path.Combine(chosen, "timeline.json");
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static bool TryParseEvent(JsonElement ev, EvidencePaths? evidencePaths, out ActivityEvent activityEvent)
    {
        activityEvent = null!;

        if (!ev.TryGetProperty("timestamp", out var tsEl) || !ev.TryGetProperty("category", out var catEl) || !ev.TryGetProperty("action", out var actEl))
            return false;

        if (!DateTime.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            return false;

        var category = catEl.GetString() ?? string.Empty;
        var action = actEl.GetString() ?? string.Empty;

        var subjectProcess = ev.TryGetProperty("subjectProcess", out var sp) ? ParseProcessKey(sp.GetString()) : null;
        var subjectUser = ev.TryGetProperty("subjectUser", out var su) ? ParseUserKey(su.GetString()) : null;
        var objectFile = ev.TryGetProperty("objectFile", out var of) ? ParseFileKey(of.GetString()) : null;
        var objectRegistry = ev.TryGetProperty("objectRegistry", out var or) ? ParseRegistryKey(or.GetString()) : null;
        var objectNetworkFlow = ev.TryGetProperty("objectNetworkFlow", out var on) ? ParseNetworkFlowKey(on.GetString()) : null;
        var objectUrl = ev.TryGetProperty("objectUrl", out var ou) ? ou.GetString() : null;
        var summary = ev.TryGetProperty("summary", out var sum) ? sum.GetString() : null;
        var confidence = ev.TryGetProperty("confidence", out var conf) ? conf.GetString() ?? "Medium" : "Medium";

        var fields = new Dictionary<string, object>();
        if (ev.TryGetProperty("fields", out var fEl) && fEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in fEl.EnumerateObject())
            {
                fields[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" :
                    prop.Value.ValueKind == JsonValueKind.Number ? prop.Value.GetRawText() : prop.Value.GetRawText();
            }
        }

        var evidence = new List<EvidenceRef>();
        if (ev.TryGetProperty("evidence", out var evArr))
        {
            foreach (var e in evArr.EnumerateArray())
            {
                var source = e.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
                var reference = e.TryGetProperty("reference", out var r) ? r.GetString() ?? "" : "";
                reference = ResolveEvidenceReference(reference, evidencePaths);
                var hash = e.TryGetProperty("hash", out var h) ? h.GetString() : null;
                DateTime? collectedAt = null;
                if (e.TryGetProperty("collectedAt", out var c) && DateTime.TryParse(c.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ca))
                    collectedAt = ca;
                evidence.Add(new EvidenceRef(source, reference, hash, collectedAt));
            }
        }

        activityEvent = new ActivityEvent
        {
            Timestamp = timestamp,
            Category = category,
            Action = action,
            SubjectProcess = subjectProcess,
            SubjectUser = subjectUser,
            ObjectFile = objectFile,
            ObjectRegistry = objectRegistry,
            ObjectNetworkFlow = objectNetworkFlow,
            ObjectUrl = objectUrl,
            Summary = summary,
            Fields = fields,
            Evidence = evidence,
            Confidence = confidence
        };
        return true;
    }

    private static string ResolveEvidenceReference(string reference, EvidencePaths? paths)
    {
        if (string.IsNullOrWhiteSpace(reference) || paths is null)
            return reference;

        if (!reference.StartsWith("raw/", StringComparison.OrdinalIgnoreCase) &&
            !reference.StartsWith("raw\\", StringComparison.OrdinalIgnoreCase))
        {
            return reference;
        }

        var normalizedReference = reference
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var resolvedPath = Path.GetFullPath(Path.Combine(paths.Value.ResolvedBaseDirectory, normalizedReference));
        return IsPathWithinDirectory(resolvedPath, paths.Value.RawDirectoryWithTrailingSeparator)
            ? resolvedPath
            : reference;
    }

    private static bool IsPathWithinDirectory(string path, string rawDirectoryWithTrailingSeparator)
    {
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(rawDirectoryWithTrailingSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static ProcessKey? ParseProcessKey(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.StartsWith("P:", StringComparison.Ordinal))
            return null;
        var rest = s.AsSpan(2);
        var i = rest.IndexOf(':');
        if (i <= 0)
            return null;
        if (!ulong.TryParse(rest[..i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bootId))
            return null;
        rest = rest[(i + 1)..];
        i = rest.IndexOf(':');
        if (i <= 0)
            return null;
        if (!uint.TryParse(rest[..i], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            return null;
        var ctStr = rest[(i + 1)..].ToString();
        if (!DateTime.TryParse(ctStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ct))
            return null;
        return new ProcessKey(bootId, pid, ct);
    }

    private static UserKey? ParseUserKey(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.StartsWith("U:", StringComparison.Ordinal))
            return null;
        try
        {
            return new UserKey(s.Substring(2));
        }
        catch
        {
            return null;
        }
    }

    private static FileKey? ParseFileKey(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.StartsWith("F:", StringComparison.Ordinal))
            return null;
        var rest = s.Substring(2);
        if (rest == "Unknown")
            return new FileKey(null, null, null, null);

        // Volume+FileId form is exactly "F:{VolumeSerial}:{FileId}". A volume serial never contains a
        // path separator, so requiring exactly two segments (and a separator-free serial) prevents a
        // path that merely contains ':' (e.g. a drive letter) from being misread as a volume serial.
        var parts = rest.Split(':');
        if (parts.Length == 2
            && parts[0].Length > 0
            && !parts[0].Contains('\\') && !parts[0].Contains('/')
            && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var fileId))
        {
            return new FileKey(parts[0], fileId, null, null);
        }

        // Path form. FileKey.ToString() appends a decorative ":{hash[..8]}" suffix when a hash is
        // present; strip it so the round-tripped Path is not corrupted. The full hash cannot be
        // recovered from the 8-char prefix and is preserved separately in entities.json / evidence.
        return new FileKey(null, null, StripDisplayHashSuffix(rest), null);
    }

    /// <summary>
    /// Removes the decorative ":{hash[..8]}" suffix that <see cref="FileKey.ToString"/> appends to the
    /// path form, if present (exactly 8 trailing hex chars after a ':'). Leaves any other path untouched.
    /// </summary>
    private static string StripDisplayHashSuffix(string pathWithMaybeHash)
    {
        var sep = pathWithMaybeHash.LastIndexOf(':');
        if (sep <= 0 || pathWithMaybeHash.Length - sep - 1 != 8)
            return pathWithMaybeHash;
        for (var i = sep + 1; i < pathWithMaybeHash.Length; i++)
        {
            if (!Uri.IsHexDigit(pathWithMaybeHash[i]))
                return pathWithMaybeHash;
        }
        return pathWithMaybeHash[..sep];
    }

    private static RegistryKey? ParseRegistryKey(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.StartsWith("R:", StringComparison.Ordinal))
            return null;
        try
        {
            return new RegistryKey(s.Substring(2));
        }
        catch
        {
            return null;
        }
    }

    private static NetworkFlowKey? ParseNetworkFlowKey(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.StartsWith("N:", StringComparison.Ordinal))
            return null;
        var rest = s.Substring(2);
        var arrow = rest.IndexOf("->", StringComparison.Ordinal);
        if (arrow < 0)
            return null;

        var left = rest.Substring(0, arrow);
        var right = rest.Substring(arrow + 2);
        if (left.Length == 0 || right.Length == 0)
            return null;

        var protoEnd = left.IndexOf(':');
        if (protoEnd <= 0 || protoEnd >= left.Length - 1)
            return null;
        var protocol = left.Substring(0, protoEnd);
        var localEp = left.Substring(protoEnd + 1);

        if (!TrySplitRemoteEndpointPidAndTime(right, out var remoteEp, out var processId, out var timeBucket))
            return null;

        return new NetworkFlowKey(protocol, localEp, remoteEp, processId, timeBucket);
    }

    /// <summary>
    /// After the <c>-&gt;</c> in <see cref="NetworkFlowKey.ToString"/>, splits
    /// <c>RemoteEndpoint:ProcessId:TimeBucket (round-trip O)</c> where endpoints may contain ':' (e.g. IPv6).
    /// </summary>
    private static bool TrySplitRemoteEndpointPidAndTime(string afterArrow, out string remoteEndpoint, out uint? processId, out DateTime timeBucket)
    {
        remoteEndpoint = "";
        processId = null;
        timeBucket = default;

        for (var sep = afterArrow.Length; sep >= 0; sep--)
        {
            if (sep > 0 && afterArrow[sep - 1] != ':')
                continue;

            var candidate = afterArrow.Substring(sep);
            if (candidate.Length == 0)
                continue;
            if (!DateTime.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timeBucket))
                continue;

            var head = sep > 0 ? afterArrow[..(sep - 1)] : "";
            if (head.Length == 0)
            {
                remoteEndpoint = "";
                processId = null;
                return true;
            }

            var lastColon = head.LastIndexOf(':');
            if (lastColon < 0)
            {
                remoteEndpoint = head;
                processId = null;
                return true;
            }

            remoteEndpoint = head[..lastColon];
            var pidStr = head[(lastColon + 1)..];
            if (pidStr.Length == 0)
                processId = null;
            else if (uint.TryParse(pidStr, NumberStyles.None, CultureInfo.InvariantCulture, out var p))
                processId = p;
            else
                return false;

            return true;
        }

        return false;
    }

    /// <summary>Parses a single event JSON (same format as timeline.json events array element). Returns null if invalid.</summary>
    public static ActivityEvent? ParseEventFromJson(string json, string? evidenceBaseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var paths = CreateEvidencePaths(evidenceBaseDirectory);
            return TryParseEvent(doc.RootElement, paths, out var ev) ? ev : null;
        }
        catch
        {
            return null;
        }
    }
}

