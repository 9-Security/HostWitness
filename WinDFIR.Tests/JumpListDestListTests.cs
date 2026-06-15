using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenMcdf;
using WinDFIR.Providers.Parsers;
using Xunit;

namespace WinDFIR.Tests;

public class JumpListDestListTests
{
    // --- Synthetic layout test (no sample needed) ---

    [Fact]
    public void Parse_Version3Entry_DecodesStreamNameTimeAndPath()
    {
        var lastMod = new DateTime(2026, 3, 12, 2, 41, 14, DateTimeKind.Utc);
        const string path = @"D:\cursor\KDFIR\KAPE";

        var entry = BuildV3Entry(entryNumber: 0xD3, hostname: "zen9ya0", filetime: lastMod.ToFileTimeUtc(), pinStatus: -1, path: path);
        var stream = BuildDestList(version: 3, entryCount: 1, entry);

        var parsed = JumpListDestListParser.Parse(stream);

        var e = Assert.Single(parsed);
        Assert.Equal(0xD3u, e.EntryId);
        Assert.Equal("d3", e.StreamName);          // lowercase hex, no padding -> CFB stream name
        Assert.Equal("zen9ya0", e.Hostname);
        Assert.Equal(lastMod, e.LastAccessTimeUtc);
        Assert.Equal(path, e.PathHint);
    }

    [Fact]
    public void Parse_ZeroFiletime_YieldsNullAccessTime()
    {
        var entry = BuildV3Entry(entryNumber: 1, hostname: "h", filetime: 0, pinStatus: -1, path: @"C:\x");
        var parsed = JumpListDestListParser.Parse(BuildDestList(3, 1, entry));
        Assert.Null(Assert.Single(parsed).LastAccessTimeUtc);
    }

    [Fact]
    public void Parse_TwoEntries_WalksVariableLengthCorrectly()
    {
        var e1 = BuildV3Entry(1, "h", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(), -1, @"C:\short");
        var e2 = BuildV3Entry(0x2a, "h", new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(), -1, @"C:\a much longer path here\file.txt");
        var stream = BuildDestList(3, 2, e1, e2);

        var parsed = JumpListDestListParser.Parse(stream);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("1", parsed[0].StreamName);
        Assert.Equal("2a", parsed[1].StreamName);
        Assert.Equal(@"C:\a much longer path here\file.txt", parsed[1].PathHint);
    }

    // --- Ground-truth validation against real samples (JLECmd-verified), gated on presence ---

    private const string RecentDir = @"D:\cursor\KDFIR\KAPE\KAPE_Extracted\C\Users\Sharlotlot\AppData\Roaming\Microsoft\Windows\Recent\AutomaticDestinations";

    [Fact]
    public void Parse_RealCursorQuickAccess_MatchesJLECmd()
    {
        var file = Path.Combine(RecentDir, "5f7b5f1e01b83767.automaticDestinations-ms");
        if (!File.Exists(file))
            return;

        var entries = JumpListDestListParser.Parse(ReadDestList(file));

        // JLECmd: single entry, EntryNumber 1, Hostname zen9ya0, Path C:\Program Files\cursor\Cursor.exe
        var e = Assert.Single(entries);
        Assert.Equal("1", e.StreamName);
        Assert.Equal("zen9ya0", e.Hostname);
        Assert.Equal(@"C:\Program Files\cursor\Cursor.exe", e.PathHint);
    }

    [Fact]
    public void Parse_RealExplorer_ResolvesEntryD3WithTimestamp()
    {
        var file = Path.Combine(RecentDir, "f01b4d95cf55d32a.automaticDestinations-ms");
        if (!File.Exists(file))
            return;

        var entries = JumpListDestListParser.Parse(ReadDestList(file));
        Assert.NotEmpty(entries);

        // JLECmd: entry D3 (211) -> Path D:\cursor\KDFIR\KAPE, LastModified 2026-03-12 02:41:14
        var d3 = entries.FirstOrDefault(x => x.StreamName == "d3");
        Assert.NotNull(d3);
        Assert.Equal(211u, d3!.EntryId);
        Assert.Equal(@"D:\cursor\KDFIR\KAPE", d3.PathHint);
        Assert.NotNull(d3.LastAccessTimeUtc);
        Assert.Equal(new DateTime(2026, 3, 12, 2, 41, 14, DateTimeKind.Utc), d3.LastAccessTimeUtc!.Value.Date.Add(new TimeSpan(2, 41, 14)));
    }

    // --- helpers ---

    private static byte[] ReadDestList(string file)
    {
        using var root = RootStorage.OpenRead(file);
        Assert.True(root.TryOpenStream("DestList", out var s));
        using var ms = new MemoryStream();
        using (s) s.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] BuildDestList(int version, int entryCount, params byte[][] entries)
    {
        using var ms = new MemoryStream();
        var header = new byte[0x20];
        BitConverter.GetBytes(version).CopyTo(header, 0);
        BitConverter.GetBytes(entryCount).CopyTo(header, 4);
        ms.Write(header, 0, header.Length);
        foreach (var e in entries)
            ms.Write(e, 0, e.Length);
        return ms.ToArray();
    }

    private static byte[] BuildV3Entry(uint entryNumber, string hostname, long filetime, int pinStatus, string path)
    {
        const int fixedSize = 0x80;
        var pathBytes = Encoding.Unicode.GetBytes(path);
        var entry = new byte[fixedSize + 2 + pathBytes.Length + 4]; // +trailer

        var host = Encoding.ASCII.GetBytes(hostname);
        Array.Copy(host, 0, entry, 0x48, Math.Min(host.Length, 16));
        BitConverter.GetBytes(entryNumber).CopyTo(entry, 0x58);
        BitConverter.GetBytes(filetime).CopyTo(entry, 0x64);
        BitConverter.GetBytes(pinStatus).CopyTo(entry, 0x70);
        BitConverter.GetBytes((ushort)(pathBytes.Length / 2)).CopyTo(entry, fixedSize);
        pathBytes.CopyTo(entry, fixedSize + 2);
        return entry;
    }
}
