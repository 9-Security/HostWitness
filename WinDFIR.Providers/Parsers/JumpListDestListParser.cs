using System;
using System.Collections.Generic;
using System.Text;

namespace WinDFIR.Providers.Parsers;

/// <summary>
/// One DestList entry. <see cref="StreamName"/> is <see cref="EntryId"/> formatted as lowercase hex with no
/// leading zeros — the name of the corresponding CFB stream inside the AutomaticDestinations file.
/// </summary>
public class JumpListDestListEntry
{
    /// <summary>Entry number (maps to the CFB stream name via lowercase hex, no padding).</summary>
    public ulong EntryId { get; set; }
    public string StreamName { get; set; } = string.Empty;

    /// <summary>Position of the entry within the DestList (0-based).</summary>
    public int MruOrder { get; set; }

    /// <summary>Last-modified/access FILETIME from the DestList entry (0x64); null when zero/invalid.</summary>
    public DateTime? LastAccessTimeUtc { get; set; }

    public int AccessCount { get; set; }
    public int PinStatus { get; set; }
    public string Hostname { get; set; } = string.Empty;

    /// <summary>Target path recorded in the DestList entry (the readable path string at the end of the entry).</summary>
    public string PathHint { get; set; } = string.Empty;
}

/// <summary>
/// Parses the <c>DestList</c> stream of an AutomaticDestinations Jump List. The DestList is a header followed
/// by variable-length entries; each entry's fixed portion carries the droids/hostname/entry-number/timestamp,
/// then a length-prefixed UTF-16 path. Layout verified against real version-6 samples (and JLECmd output).
/// </summary>
/// <remarks>
/// Header (0x20 bytes): version(0), entryCount(4), pinnedCount(8), …, lastEntryNumber(0x10).
/// Entry fixed portion: checksum(0x00,8), NewVolumeID(0x08,16), NewObjectID(0x18,16), BirthVolumeID(0x28,16),
/// BirthObjectID(0x38,16), Hostname(0x48,16 ASCII), <b>EntryNumber(0x58,4)</b>, …, <b>LastModified FILETIME
/// (0x64,8)</b>, …, PinStatus(0x70,4). For version ≥ 3 the fixed portion is 0x80 bytes and each entry has a
/// 4-byte trailer after the path; for version 1 the fixed portion is 0x70 bytes with no trailer. The path is a
/// 2-byte character count immediately after the fixed portion, then that many UTF-16 code units.
/// </remarks>
public static class JumpListDestListParser
{
    private const int HeaderSize = 0x20;
    private const int FixedV1 = 0x70;
    private const int FixedV3Plus = 0x80;
    private const int MaxPathChars = 0x8000; // sanity bound against corrupt length fields

    public static List<JumpListDestListEntry> Parse(byte[] bytes)
    {
        var entries = new List<JumpListDestListEntry>();
        if (bytes == null || bytes.Length < HeaderSize + 4)
            return entries;

        var version = BitConverter.ToInt32(bytes, 0);
        var fixedSize = version >= 3 ? FixedV3Plus : FixedV1;
        var trailer = version >= 3 ? 4 : 0;

        var offset = HeaderSize;
        var order = 0;
        while (offset + fixedSize + 2 <= bytes.Length)
        {
            if (!TryParseEntry(bytes, offset, version, fixedSize, trailer, order, out var entry, out var consumed) || consumed <= 0)
                break;

            entries.Add(entry);
            offset += consumed;
            order++;
        }

        return entries;
    }

    private static bool TryParseEntry(byte[] bytes, int start, int version, int fixedSize, int trailer, int order,
        out JumpListDestListEntry entry, out int consumed)
    {
        entry = new JumpListDestListEntry { MruOrder = order };
        consumed = 0;

        // Path length (UTF-16 code units) immediately follows the fixed portion.
        var pathLenOffset = start + fixedSize;
        if (pathLenOffset + 2 > bytes.Length)
            return false;

        var pathChars = BitConverter.ToUInt16(bytes, pathLenOffset);
        if (pathChars > MaxPathChars)
            return false;

        var pathByteOffset = pathLenOffset + 2;
        var pathBytes = pathChars * 2;
        if (pathByteOffset + pathBytes + trailer > bytes.Length)
            return false;

        var entryNumber = BitConverter.ToUInt32(bytes, start + 0x58);
        entry.EntryId = entryNumber;
        entry.StreamName = entryNumber.ToString("x"); // lowercase hex, no leading zeros — matches CFB stream name

        entry.Hostname = ReadAsciiZ(bytes, start + 0x48, 16);

        var filetime = BitConverter.ToInt64(bytes, start + 0x64);
        entry.LastAccessTimeUtc = TryFileTime(filetime);

        // PinStatus lives at 0x70 in the v3+ layout (within the extended fixed portion).
        if (version >= 3)
            entry.PinStatus = BitConverter.ToInt32(bytes, start + 0x70);

        if (pathBytes > 0)
        {
            try
            {
                entry.PathHint = Encoding.Unicode.GetString(bytes, pathByteOffset, pathBytes).TrimEnd('\0');
            }
            catch
            {
                // leave PathHint empty on decode failure
            }
        }

        consumed = fixedSize + 2 + pathBytes + trailer;
        return true;
    }

    private static DateTime? TryFileTime(long filetime)
    {
        if (filetime <= 0)
            return null;
        try
        {
            var dt = DateTime.FromFileTimeUtc(filetime);
            if (dt.Year < 1970 || dt.Year > 2100)
                return null;
            return dt;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string ReadAsciiZ(byte[] bytes, int offset, int maxLen)
    {
        if (offset < 0 || offset >= bytes.Length)
            return string.Empty;
        var end = Math.Min(offset + maxLen, bytes.Length);
        var sb = new StringBuilder();
        for (var i = offset; i < end; i++)
        {
            var c = bytes[i];
            if (c == 0)
                break;
            if (c >= 0x20 && c < 0x7F)
                sb.Append((char)c);
        }
        return sb.ToString();
    }
}
