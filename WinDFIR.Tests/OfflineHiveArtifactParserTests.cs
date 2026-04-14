using System;
using System.Linq;
using System.Text;
using WinDFIR.Providers.Parsers;
using Xunit;

namespace WinDFIR.Tests;

public class OfflineHiveArtifactParserTests
{
    [Fact]
    public void UserAssistParser_Rot13_DecodesLetters()
    {
        Assert.Equal("Uryyb", UserAssistParser.Rot13("Hello"));
        Assert.Equal("Grfg", UserAssistParser.Rot13("Test"));
    }

    [Fact]
    public void UserAssistParser_TryDecode_ParsesCountAndFileTime()
    {
        var ft = new DateTime(2021, 6, 15, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var data = new byte[16];
        BitConverter.GetBytes(3u).CopyTo(data, 0);
        BitConverter.GetBytes(42u).CopyTo(data, 4);
        BitConverter.GetBytes((ulong)ft).CopyTo(data, 8);

        Assert.True(UserAssistParser.TryDecode("UEME_RUNPIDL:Z:\\grfg.exe", data, out var d));
        Assert.Equal(42u, d.RunCount);
        Assert.Equal(3u, d.FocusCount);
        Assert.True(d.LastExecutionUtc.HasValue);
        Assert.Equal(DateTimeKind.Utc, d.LastExecutionUtc!.Value.Kind);
        Assert.Contains("HRZR", d.DecodedName, StringComparison.Ordinal);
    }

    [Fact]
    public void AppCompatCacheParser_Parse_WinXpStyle_PathBoundedPerRecord()
    {
        const int stride = 400;
        const int pathCap = 392;
        const int ftOff = 392;
        var blob = new byte[stride * 2];
        WriteAsciiPath(blob, 0, @"C:\XP\one.exe", pathCap);
        WriteFileTime(blob, ftOff, new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteAsciiPath(blob, stride, @"D:\XP\two.exe", pathCap);
        WriteFileTime(blob, stride + ftOff, new DateTime(2010, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var entries = AppCompatCacheParser.Parse(blob);
        Assert.True(entries.Count >= 2);
        Assert.Contains(entries, e => e.FilePath.Contains("one.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.FilePath.Contains("two.exe", StringComparison.OrdinalIgnoreCase));
        Assert.All(entries, e => Assert.Equal("WinXP_400", e.ParseFormat));
    }

    private static void WriteAsciiPath(byte[] blob, int offset, string path, int maxBytes)
    {
        Array.Clear(blob, offset, maxBytes);
        var enc = Encoding.ASCII.GetBytes(path + "\0");
        Array.Copy(enc, 0, blob, offset, Math.Min(enc.Length, maxBytes));
    }

    [Fact]
    public void AppCompatCacheParser_Parse_Win7StyleExtractsPaths()
    {
        var blob = new byte[128 + 552 * 3];
        WriteUnicodePath(blob, 128, @"C:\Windows\System32\cmd.exe");
        WriteFileTime(blob, 128 + 520, new DateTime(2019, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteUnicodePath(blob, 128 + 552, @"C:\Temp\malware.exe");
        WriteFileTime(blob, 128 + 552 + 520, new DateTime(2019, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteUnicodePath(blob, 128 + 552 * 2, @"D:\Tools\run.exe");
        WriteFileTime(blob, 128 + 552 * 2 + 520, new DateTime(2019, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        var entries = AppCompatCacheParser.Parse(blob);
        Assert.True(entries.Count >= 3);
        Assert.Contains(entries, e => e.FilePath.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.FilePath.Contains("malware.exe", StringComparison.OrdinalIgnoreCase));
        Assert.All(entries, e => Assert.Equal("Win7_552", e.ParseFormat));
    }

    private static void WriteUnicodePath(byte[] blob, int offset, string path)
    {
        var bytes = Encoding.Unicode.GetBytes(path + "\0");
        var len = Math.Min(bytes.Length, 520);
        Array.Clear(blob, offset, 520);
        Array.Copy(bytes, 0, blob, offset, len);
    }

    private static void WriteFileTime(byte[] blob, int offset, DateTime utc)
    {
        var ft = utc.ToFileTimeUtc();
        BitConverter.GetBytes((ulong)ft).CopyTo(blob, offset);
    }
}
