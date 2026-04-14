using System.Text;

namespace WinDFIR.Providers.Parsers;

public sealed class ShimCacheEntry
{
    public string FilePath { get; init; } = string.Empty;
    public DateTime? LastModifiedUtc { get; init; }
    public DateTime? LastUpdateUtc { get; init; }
    /// <summary>Identifies which parser path produced the row (e.g. Win7_552, Win10_Variable).</summary>
    public string ParseFormat { get; init; } = string.Empty;
}

/// <summary>
/// Best-effort AppCompatCache (ShimCache) binary decoding. Multiple Windows versions use different layouts;
/// this implementation tries a small ordered set of documented fixed strides and a variable-length pass.
/// </summary>
public static class AppCompatCacheParser
{
    private const int Win7Stride = 552;
    private const int Win7PathBytes = 520;
    /// <summary>Windows XP fixed record size (documented). Path + FILETIME must fit entirely inside each stride.</summary>
    private const int WinXpStride = 400;
    /// <summary>ASCII path occupies bytes [0, WinXpFileTimeOffset); FILETIME is the last 8 bytes of the record.</summary>
    private const int WinXpFileTimeOffset = WinXpStride - 8;
    private const int WinXpPathMaxBytes = WinXpFileTimeOffset;

    public static IReadOnlyList<ShimCacheEntry> Parse(byte[]? data)
    {
        var results = new List<ShimCacheEntry>();
        if (data == null || data.Length < 80)
            return results;

        foreach (var headerSkip in new[] { 128, 48, 0 })
        {
            if (TryFixedStrideUnicode(data, headerSkip, Win7Stride, Win7PathBytes, 520, "Win7_552", results))
                return results;
        }

        if (TryFixedStrideAnsi(data, 0, WinXpStride, WinXpPathMaxBytes, WinXpFileTimeOffset, "WinXP_400", results))
            return results;

        if (TryVariableWin10Style(data, results))
            return results;

        return results;
    }

    private static bool TryFixedStrideUnicode(byte[] data, int headerSkip, int stride, int pathBytes, int fileTimeOffset,
        string format, List<ShimCacheEntry> results)
    {
        if (headerSkip + stride > data.Length || stride < 16)
            return false;
        if (fileTimeOffset < 0 || fileTimeOffset + 16 > stride || pathBytes > stride || pathBytes % 2 != 0)
            return false;

        var entries = new List<ShimCacheEntry>();
        for (var offset = headerSkip; offset + stride <= data.Length; offset += stride)
        {
            var path = ReadUnicodePath(data, offset, Math.Min(pathBytes, fileTimeOffset));
            if (!IsPlausiblePath(path))
                continue;

            DateTime? mod = null;
            DateTime? upd = null;
            if (offset + fileTimeOffset + 16 <= offset + stride)
            {
                var ft1 = BitConverter.ToUInt64(data, offset + fileTimeOffset);
                var ft2 = BitConverter.ToUInt64(data, offset + fileTimeOffset + 8);
                if (TryFileTimeUtc(ft1, out var t1))
                    mod = t1;
                if (TryFileTimeUtc(ft2, out var t2))
                    upd = t2;
            }

            entries.Add(new ShimCacheEntry
            {
                FilePath = path,
                LastModifiedUtc = mod,
                LastUpdateUtc = upd,
                ParseFormat = format
            });
        }

        if (entries.Count < 2)
            return false;

        results.AddRange(entries);
        return true;
    }

    private static bool TryFixedStrideAnsi(byte[] data, int headerSkip, int stride, int pathBytes, int fileTimeOffset,
        string format, List<ShimCacheEntry> results)
    {
        if (headerSkip + stride > data.Length || stride < 16)
            return false;
        // Path and FILETIME must stay within [offset, offset + stride) to avoid cross-record bleed.
        if (fileTimeOffset < 0 || fileTimeOffset + 8 > stride)
            return false;
        var pathCap = Math.Min(pathBytes, fileTimeOffset);
        if (pathCap < 8)
            return false;

        var entries = new List<ShimCacheEntry>();
        for (var offset = headerSkip; offset + stride <= data.Length; offset += stride)
        {
            var path = ReadAsciiPath(data, offset, pathCap);
            if (!IsPlausiblePath(path))
                continue;

            DateTime? mod = null;
            var ft = BitConverter.ToUInt64(data, offset + fileTimeOffset);
            if (TryFileTimeUtc(ft, out var t))
                mod = t;

            entries.Add(new ShimCacheEntry
            {
                FilePath = path,
                LastModifiedUtc = mod,
                ParseFormat = format
            });
        }

        if (entries.Count < 2)
            return false;

        results.AddRange(entries);
        return true;
    }

    /// <summary>Lightweight variable-size scan used when fixed strides fail (Win10+ style length-prefixed blocks).</summary>
    private static bool TryVariableWin10Style(byte[] data, List<ShimCacheEntry> results)
    {
        var entries = new List<ShimCacheEntry>();
        foreach (var start in new[] { 128, 48, 32 })
        {
            if (start >= data.Length)
                continue;
            var pos = start;
            var safety = 0;
            while (pos + 32 < data.Length && safety++ < 4096)
            {
                var blockLen = (int)BitConverter.ToUInt32(data, pos);
                if (blockLen < 32 || blockLen > 8192 || pos + blockLen > data.Length)
                {
                    pos += 4;
                    continue;
                }

                var path = ExtractUtf16PathFromBlock(data, pos + 16, blockLen - 16);
                if (IsPlausiblePath(path))
                {
                    DateTime? t = null;
                    for (var fp = pos + 16; fp + 8 <= pos + blockLen; fp += 8)
                    {
                        var ft = BitConverter.ToUInt64(data, fp);
                        if (TryFileTimeUtc(ft, out var dt))
                        {
                            t = dt;
                            break;
                        }
                    }

                    entries.Add(new ShimCacheEntry
                    {
                        FilePath = path,
                        LastModifiedUtc = t,
                        ParseFormat = "Win10_Variable"
                    });
                }

                pos += blockLen;
            }

            if (entries.Count >= 2)
            {
                results.AddRange(entries);
                return true;
            }

            entries.Clear();
        }

        return false;
    }

    private static string ExtractUtf16PathFromBlock(byte[] data, int offset, int maxLen)
    {
        var len = Math.Min(maxLen, data.Length - offset);
        if (len <= 0)
            return string.Empty;
        len -= len % 2;
        try
        {
            var s = Encoding.Unicode.GetString(data, offset, len);
            return s.Split('\0')[0].Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadUnicodePath(byte[] data, int offset, int maxBytes)
    {
        var len = Math.Min(maxBytes, data.Length - offset);
        if (len <= 0)
            return string.Empty;
        len -= len % 2;
        try
        {
            var s = Encoding.Unicode.GetString(data, offset, len);
            return s.Split('\0')[0].Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadAsciiPath(byte[] data, int offset, int maxBytes)
    {
        var len = Math.Min(maxBytes, data.Length - offset);
        if (len <= 0)
            return string.Empty;
        var sb = new StringBuilder();
        for (var i = 0; i < len; i++)
        {
            var b = data[offset + i];
            if (b == 0)
                break;
            if (b >= 32 && b < 127)
                sb.Append((char)b);
            else
                break;
        }

        return sb.ToString().Trim();
    }

    private static bool IsPlausiblePath(string p)
    {
        if (string.IsNullOrWhiteSpace(p) || p.Length < 4)
            return false;
        if (p.Count(char.IsControl) > p.Length / 2)
            return false;
        return p.Contains('\\', StringComparison.Ordinal)
 || p.Contains(':', StringComparison.Ordinal)
               || p.StartsWith("??\\", StringComparison.Ordinal)
               || p.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase)
               || p.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryFileTimeUtc(ulong fileTime, out DateTime utc)
    {
        utc = default;
        if (fileTime == 0)
            return false;
        try
        {
            utc = DateTime.FromFileTimeUtc((long)fileTime);
            if (utc.Year < 1990 || utc.Year > 2038)
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
