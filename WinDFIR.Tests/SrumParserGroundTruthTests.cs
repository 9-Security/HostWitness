using System;
using System.IO;
using System.Linq;
using WinDFIR.Providers.Parsers;
using Xunit;

namespace WinDFIR.Tests;

// Ground-truth validation of the SRUM parser against a real SRUDB.dat whose contents were independently
// decoded by SrumECmd (Eric Zimmerman). Skips when the sample is absent so the suite stays portable.
public class SrumParserGroundTruthTests
{
    private const string SrudbPath = @"D:\cursor\KDFIR\KAPE\KAPE_Extracted\C\Windows\System32\SRU\SRUDB.dat";

    // SRUDB (page 4096) and qmgr.db (16384) cannot both open in one process (ESE page size is a per-process
    // global). Return null when another ESE DB with a different page size was opened first this test run.
    private static System.Collections.Generic.List<SrumRecord>? TryParse(int cap, out System.Collections.Generic.List<string> truncated)
    {
        truncated = new System.Collections.Generic.List<string>();
        try
        {
            return SrumParser.Parse(SrudbPath, cap, out truncated).ToList();
        }
        catch (EseDatabaseReader.EsePageSizeConflictException)
        {
            return null;
        }
    }

    [Fact]
    public void Parse_NetworkDataUsage_MatchesSrumECmdRow()
    {
        if (!File.Exists(SrudbPath))
            return; // sample not present

        var records = TryParse(0, out _);
        if (records == null)
            return; // ESE page-size conflict this run
        Assert.NotEmpty(records);

        // SrumECmd ground truth for Network Data Usage Id=103700:
        //   Timestamp 2026-01-11 04:25:00, BytesReceived 981993, BytesSent 11018,
        //   ExeInfo Microsoft.DesktopAppInstaller..., Sid ...-1001
        var net = records.Where(r => r.ProviderName == "Network Data Usage").ToList();
        Assert.NotEmpty(net);

        var match = net.FirstOrDefault(r =>
            r.Fields.TryGetValue("BytesRecvd", out var br) && Convert.ToInt64(br) == 981993L &&
            r.Fields.TryGetValue("BytesSent", out var bs) && Convert.ToInt64(bs) == 11018L);

        Assert.NotNull(match);
        Assert.Equal(new DateTime(2026, 1, 11, 4, 25, 0, DateTimeKind.Utc), match!.TimestampUtc);
        Assert.Contains("DesktopAppInstaller", match.App ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("-1001", match.UserSid ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ResolvesServiceAndExePaths()
    {
        if (!File.Exists(SrudbPath))
            return;

        var records = TryParse(0, out _);
        if (records == null)
            return;

        // SrumECmd shows AppId 2731 -> \device\harddiskvolume3\windows\system32\sihclient.exe
        var sih = records.FirstOrDefault(r => (r.App ?? "").Contains("sihclient.exe", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(sih);

        // App identifiers resolve to non-empty strings for the vast majority of rows.
        var resolved = records.Count(r => !string.IsNullOrEmpty(r.App));
        Assert.True(resolved > records.Count / 2, $"Expected most apps resolved; got {resolved}/{records.Count}.");
    }

    [Fact]
    public void Parse_PerTableCap_LimitsAndReportsTruncation()
    {
        if (!File.Exists(SrudbPath))
            return;

        var capped = TryParse(10, out var truncated);
        if (capped == null)
            return;

        // Each known provider table present yields at most 10 rows under the cap.
        foreach (var group in capped.GroupBy(r => r.ProviderName))
            Assert.True(group.Count() <= 10, $"{group.Key} exceeded cap: {group.Count()}");

        // At least one large table (e.g. Network Data Usage) should report truncation.
        Assert.NotEmpty(truncated);
    }
}
