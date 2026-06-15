using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using WinDFIR.Providers.Parsers;
using Xunit;

namespace WinDFIR.Tests.Validation;

/// <summary>
/// Large-scale differential validation of the SRUM parser against SrumECmd (Eric Zimmerman) output: instead of
/// spot-checking one row, this confirms our Network Data Usage records agree with SrumECmd's CSV across the
/// whole table (count + per-row (timestamp, bytesSent, bytesRecvd) multiset). Corpus pattern: drop another
/// SRUDB.dat + its SrumECmd CSV in and add a case. Gated on sample presence.
/// </summary>
public class SrumDifferentialTests
{
    private const string SrudbPath = @"D:\cursor\KDFIR\KAPE\KAPE_Extracted\C\Windows\System32\SRU\SRUDB.dat";
    private const string NetworkUsagesCsv = @"D:\cursor\KDFIR\KAPE\KAPE_Analysis\SRUMDatabase\20260312045347_SrumECmd_NetworkUsages_Output.csv";

    [Fact]
    public void NetworkDataUsage_AgreesWithSrumECmd_AtScale()
    {
        if (!File.Exists(SrudbPath) || !File.Exists(NetworkUsagesCsv))
            return;

        // Our records.
        List<string> ours;
        try
        {
            ours = SrumParser.Parse(SrudbPath, perTableCap: 0, out _)
                .Where(r => r.ProviderName == "Network Data Usage")
                .Select(Key)
                .Where(k => k != null)
                .Select(k => k!)
                .ToList();
        }
        catch (EseDatabaseReader.EsePageSizeConflictException)
        {
            return; // another ESE DB with a different page size opened first this test process
        }

        // Ground truth.
        var csvRows = CsvGroundTruth.Read(NetworkUsagesCsv);
        var truth = csvRows.Select(KeyFromCsv).Where(k => k != null).Select(k => k!).ToList();

        Assert.NotEmpty(ours);
        Assert.NotEmpty(truth);

        // Count agreement within 2%.
        var countDelta = Math.Abs(ours.Count - truth.Count) / (double)truth.Count;
        Assert.True(countDelta <= 0.02, $"Row count diverges: ours={ours.Count}, SrumECmd={truth.Count} ({countDelta:P1}).");

        // Multiset coverage: at least 98% of SrumECmd rows have a matching (ts,sent,recvd) record in ours.
        var bag = new Dictionary<string, int>(ours.Count);
        foreach (var k in ours)
            bag[k] = bag.TryGetValue(k, out var c) ? c + 1 : 1;

        var matched = 0;
        foreach (var k in truth)
        {
            if (bag.TryGetValue(k, out var c) && c > 0) { bag[k] = c - 1; matched++; }
        }
        var coverage = matched / (double)truth.Count;
        Assert.True(coverage >= 0.98, $"Only {coverage:P1} of SrumECmd rows matched ours ({matched}/{truth.Count}).");
    }

    private static string? Key(SrumRecord r)
    {
        if (!r.Fields.TryGetValue("BytesSent", out var s) || !r.Fields.TryGetValue("BytesRecvd", out var rc))
            return null;
        return $"{r.TimestampUtc:yyyy-MM-dd HH:mm:ss}|{Convert.ToInt64(s)}|{Convert.ToInt64(rc)}";
    }

    private static string? KeyFromCsv(Dictionary<string, string> row)
    {
        if (!row.TryGetValue("Timestamp", out var ts) ||
            !row.TryGetValue("BytesSent", out var sent) ||
            !row.TryGetValue("BytesReceived", out var recvd))
            return null;
        if (!DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return null;
        if (!long.TryParse(sent, out var s) || !long.TryParse(recvd, out var r))
            return null;
        return $"{dt:yyyy-MM-dd HH:mm:ss}|{s}|{r}";
    }
}
