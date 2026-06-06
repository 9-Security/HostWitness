namespace WinDFIR.Providers.Parsers;

/// <summary>
/// Decodes Background/Desktop Activity Moderator (BAM/DAM) last-execution entries from the SYSTEM hive.
/// Layout: <c>...\Services\bam\State\UserSettings\&lt;SID&gt;</c> (or older <c>...\Services\bam\UserSettings\&lt;SID&gt;</c>,
/// and the <c>dam</c> equivalent). Under each per-SID key, a value's <b>name</b> is the executable's NT path and
/// its <b>data</b> begins with an 8-byte little-endian FILETIME of the last execution.
/// </summary>
public static class BamDamParser
{
    /// <summary>
    /// True if <paramref name="keyPathRelative"/> is a BAM/DAM per-user (SID) settings key.
    /// <paramref name="component"/> is "BAM" or "DAM".
    /// </summary>
    public static bool IsBamUserSettingsKey(string? keyPathRelative, out string component)
    {
        component = string.Empty;
        if (string.IsNullOrEmpty(keyPathRelative))
            return false;

        var lower = keyPathRelative.ToLowerInvariant();
        var isBam = lower.Contains(@"\services\bam\");
        var isDam = lower.Contains(@"\services\dam\");
        if (!isBam && !isDam)
            return false;
        if (!lower.Contains(@"\usersettings\"))
            return false;

        var leaf = LeafSegment(keyPathRelative);
        if (!leaf.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            return false;

        component = isBam ? "BAM" : "DAM";
        return true;
    }

    /// <summary>
    /// Decodes the last-execution FILETIME for an executable entry value. Returns false for non-executable
    /// bookkeeping values (Version, SequenceNumber) or out-of-range/zero timestamps.
    /// </summary>
    public static bool TryDecodeLastExecution(string? valueName, byte[]? raw, out DateTime lastExecutionUtc)
    {
        lastExecutionUtc = default;

        // Executable entries have a path-like name; bookkeeping values (Version, SequenceNumber) do not.
        if (string.IsNullOrEmpty(valueName) || !valueName.Contains('\\'))
            return false;
        if (raw is null || raw.Length < 8)
            return false;

        try
        {
            var filetime = BitConverter.ToInt64(raw, 0);
            if (filetime <= 0)
                return false;

            var decoded = DateTime.FromFileTimeUtc(filetime);
            // Reject implausible values (corrupt data / not a real FILETIME).
            if (decoded.Year < 2000 || decoded.Year > 2100)
                return false;

            lastExecutionUtc = decoded;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Returns the SID (leaf key segment) for a BAM/DAM UserSettings key.</summary>
    public static string ExtractUserSid(string keyPathRelative) => LeafSegment(keyPathRelative);

    private static string LeafSegment(string path)
    {
        var idx = path.LastIndexOf('\\');
        return idx >= 0 && idx < path.Length - 1 ? path[(idx + 1)..] : path;
    }
}
