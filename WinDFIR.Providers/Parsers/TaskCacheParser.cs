namespace WinDFIR.Providers.Parsers;

/// <summary>
/// Decodes Scheduled Task registration metadata from the SOFTWARE hive TaskCache.
/// Layout: <c>Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tasks\{GUID}</c>. Each per-task
/// (brace-GUID) key carries a <c>Path</c> value (REG_SZ, the task's tree path) and a <c>DynamicInfo</c>
/// value (REG_BINARY) whose layout embeds FILETIMEs for creation/registration, last run, and last
/// successful completion.
/// </summary>
/// <remarks>
/// Deliberately scoped to the low-risk fields only: the <c>Path</c> string and the FILETIMEs in
/// <c>DynamicInfo</c>. The structured <c>Actions</c>/<c>Triggers</c> binary blobs are NOT decoded here —
/// the on-disk Task XML provider already surfaces the command line, and a mis-parsed action blob would
/// inject false evidence. Every FILETIME read is range-checked (year 2000–2100), so a wrong offset on an
/// unexpected blob layout yields a rejected (null) value rather than a fabricated timestamp.
/// </remarks>
public static class TaskCacheParser
{
    // Observed DynamicInfo offsets (Win8+ through Win10/11): a 4-byte version header, then FILETIMEs.
    // Larger blobs (Win10 1809+) append the last-successful-completion time at 0x1C.
    private const int CreatedOffset = 0x04;       // task registration / creation
    private const int LastRunOffset = 0x0C;       // last run start
    private const int LastSuccessOffset = 0x1C;   // last successful completion (newer builds only)

    /// <summary>
    /// True if <paramref name="keyPathRelative"/> is a TaskCache per-task key whose leaf is a brace GUID.
    /// <paramref name="taskGuid"/> receives that GUID (including braces).
    /// </summary>
    public static bool IsTaskCacheTaskKey(string? keyPathRelative, out string taskGuid)
    {
        taskGuid = string.Empty;
        if (string.IsNullOrEmpty(keyPathRelative))
            return false;

        if (!keyPathRelative.Contains(@"\Schedule\TaskCache\Tasks\", StringComparison.OrdinalIgnoreCase))
            return false;

        var leaf = LeafSegment(keyPathRelative);
        if (leaf.Length < 2 || leaf[0] != '{' || leaf[^1] != '}')
            return false;

        taskGuid = leaf;
        return true;
    }

    /// <summary>
    /// Decodes the FILETIMEs embedded in a TaskCache <c>DynamicInfo</c> value. Returns true if at least
    /// one plausible timestamp was recovered. Out-of-range or zero FILETIMEs (and offsets past the end of
    /// short blobs) yield null for that field.
    /// </summary>
    public static bool TryDecodeDynamicInfo(byte[]? raw, out DateTime? createdUtc, out DateTime? lastRunUtc, out DateTime? lastSuccessUtc)
    {
        createdUtc = null;
        lastRunUtc = null;
        lastSuccessUtc = null;

        if (raw is null || raw.Length < CreatedOffset + 8)
            return false;

        createdUtc = ReadFileTime(raw, CreatedOffset);
        lastRunUtc = ReadFileTime(raw, LastRunOffset);
        lastSuccessUtc = ReadFileTime(raw, LastSuccessOffset);

        return createdUtc.HasValue || lastRunUtc.HasValue || lastSuccessUtc.HasValue;
    }

    private static DateTime? ReadFileTime(byte[] raw, int offset)
    {
        if (offset < 0 || offset + 8 > raw.Length)
            return null;

        try
        {
            var filetime = BitConverter.ToInt64(raw, offset);
            if (filetime <= 0)
                return null;

            var decoded = DateTime.FromFileTimeUtc(filetime);
            if (decoded.Year < 2000 || decoded.Year > 2100)
                return null;

            return decoded;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string LeafSegment(string path)
    {
        var idx = path.LastIndexOf('\\');
        return idx >= 0 && idx < path.Length - 1 ? path[(idx + 1)..] : path;
    }
}
