namespace WinDFIR.Core.Normalization;

/// <summary>
/// Centralized time normalization utilities.
/// Handles conversion from various Windows time formats to UTC DateTime.
/// </summary>
public static class TimeNormalizer
{
    /// <summary>
    /// Converts Windows FILETIME (100-nanosecond intervals since 1601-01-01) to UTC DateTime.
    /// </summary>
    public static DateTime FromFileTime(ulong fileTime)
    {
        return DateTime.FromFileTimeUtc((long)fileTime);
    }

    /// <summary>
    /// Converts Unix timestamp (seconds since 1970-01-01) to UTC DateTime.
    /// </summary>
    public static DateTime FromUnixTimeSeconds(long unixSeconds)
    {
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
    }

    /// <summary>
    /// Converts Unix timestamp (milliseconds since 1970-01-01) to UTC DateTime.
    /// </summary>
    public static DateTime FromUnixTimeMilliseconds(long unixMilliseconds)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime;
    }

    /// <summary>
    /// Normalizes a DateTime to UTC, preserving the instant if already UTC.
    /// </summary>
    public static DateTime NormalizeToUtc(DateTime dateTime)
    {
        if (dateTime.Kind == DateTimeKind.Utc)
            return dateTime;
        
        if (dateTime.Kind == DateTimeKind.Local)
            return dateTime.ToUniversalTime();
        
        // Unspecified: assume UTC
        return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
    }
}
