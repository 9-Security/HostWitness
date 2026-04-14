using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WinDFIR.Providers.Parsers;

/// <summary>
/// DestList entry: StreamName is the hex representation of EntryId for StreamName/EntryId correspondence.
/// Sort by LastAccessTimeUtc (desc) for MRU ordering credibility.
/// </summary>
public class JumpListDestListEntry
{
    /// <summary>Entry ID (raw); StreamName is its hex representation for StreamName/EntryId mapping.</summary>
    public ulong EntryId { get; set; }
    public string StreamName { get; set; } = string.Empty;
    public int MruOrder { get; set; }
    public DateTime? LastAccessTimeUtc { get; set; }
    public int AccessCount { get; set; }
    public int PinStatus { get; set; }
    public string PathHint { get; set; } = string.Empty;
}

public static class JumpListDestListParser
{
    private static readonly int[] CandidateEntrySizes = { 0x78, 0x80, 0x88, 0x90 };

    public static List<JumpListDestListEntry> Parse(byte[] bytes)
    {
        var entries = new List<JumpListDestListEntry>();
        if (bytes.Length < 0x20)
            return entries;

        var headerSize = 0x20;
        var entrySize = GuessEntrySize(bytes.Length - headerSize);
        if (entrySize == 0)
            return entries;

        var offset = headerSize;
        var order = 0;
        while (offset + entrySize <= bytes.Length)
        {
            var entryBytes = new byte[entrySize];
            Buffer.BlockCopy(bytes, offset, entryBytes, 0, entrySize);
            var entry = ParseEntry(entryBytes, order);
            if (!string.IsNullOrWhiteSpace(entry.StreamName))
            {
                entries.Add(entry);
            }

            order++;
            offset += entrySize;
        }

        entries.Sort((a, b) => CompareDestListByLastAccess(b, a));
        return entries;
    }

    private static int CompareDestListByLastAccess(JumpListDestListEntry a, JumpListDestListEntry b)
    {
        var ta = a.LastAccessTimeUtc ?? DateTime.MinValue;
        var tb = b.LastAccessTimeUtc ?? DateTime.MinValue;
        var c = ta.CompareTo(tb);
        return c != 0 ? c : a.MruOrder.CompareTo(b.MruOrder);
    }

    private static int GuessEntrySize(int payloadLength)
    {
        foreach (var size in CandidateEntrySizes)
        {
            if (payloadLength % size == 0)
                return size;
        }
        return 0;
    }

    private static JumpListDestListEntry ParseEntry(byte[] entryBytes, int order)
    {
        var entry = new JumpListDestListEntry
        {
            MruOrder = order
        };

        if (entryBytes.Length < 0x24)
            return entry;

        var entryId = BitConverter.ToUInt64(entryBytes, 0);
        entry.EntryId = entryId;
        var entryIdBytes = BitConverter.GetBytes(entryId);
        var entryIdHex = BitConverter.ToString(entryIdBytes).Replace("-", "").ToLowerInvariant();
        entry.StreamName = entryIdHex;

        var lastAccess = BitConverter.ToInt64(entryBytes, 0x10);
        if (lastAccess > 0)
        {
            entry.LastAccessTimeUtc = DateTime.FromFileTimeUtc(lastAccess);
        }

        entry.AccessCount = BitConverter.ToInt32(entryBytes, 0x18);
        entry.PinStatus = BitConverter.ToInt32(entryBytes, 0x1C);

        var pathLengthChars = BitConverter.ToInt32(entryBytes, 0x20);
        var pathOffset = 0x24;
        var byteLength = pathLengthChars * 2;
        if (pathLengthChars > 0 && pathOffset + byteLength <= entryBytes.Length)
        {
            try
            {
                var path = Encoding.Unicode.GetString(entryBytes, pathOffset, byteLength).TrimEnd('\0');
                entry.PathHint = path;
            }
            catch
            {
                // ignore invalid path hint
            }
        }

        return entry;
    }
}
