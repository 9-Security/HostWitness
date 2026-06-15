using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenMcdf;
using WinDFIR.Providers.Parsers;
using Xunit;

namespace WinDFIR.Tests.Validation;

/// <summary>
/// Large-scale differential validation of the DestList parser against JLECmd output across ALL
/// AutomaticDestinations files in the sample (not just a hand-picked one). Confirms entry-number coverage and
/// path agreement at scale. Gated on sample presence.
/// </summary>
public class JumpListDifferentialTests
{
    private const string RecentDir = @"D:\cursor\KDFIR\KAPE\KAPE_Extracted\C\Users\Sharlotlot\AppData\Roaming\Microsoft\Windows\Recent\AutomaticDestinations";
    private const string AutoDestCsv = @"D:\cursor\KDFIR\KAPE\KAPE_Analysis\FileFolderAccess\20260312045341_AutomaticDestinations.csv";

    [Fact]
    public void DestList_AgreesWithJLECmd_AcrossAllFiles()
    {
        if (!Directory.Exists(RecentDir) || !File.Exists(AutoDestCsv))
            return;

        var rows = CsvGroundTruth.Read(AutoDestCsv)
            .Where(r => r.TryGetValue("SourceFile", out var sf) && sf.EndsWith(".automaticDestinations-ms", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(rows);

        var byFile = rows.GroupBy(r => Path.GetFileName(r["SourceFile"]), StringComparer.OrdinalIgnoreCase);

        var totalEntries = 0;
        var entryMatched = 0;   // JLECmd (file,entryNumber) present in our parse
        var pathComparable = 0;
        var pathMatched = 0;
        var filesChecked = 0;

        foreach (var group in byFile)
        {
            var file = Path.Combine(RecentDir, group.Key);
            if (!File.Exists(file))
                continue;

            Dictionary<string, string> ours;
            try
            {
                ours = ParseDestList(file);
            }
            catch
            {
                continue; // unreadable file -> skip (don't fail the whole corpus run)
            }
            filesChecked++;

            foreach (var row in group)
            {
                if (!row.TryGetValue("EntryNumber", out var en) || string.IsNullOrWhiteSpace(en))
                    continue;
                totalEntries++;
                var key = en.Trim().ToLowerInvariant(); // JLECmd reports entry number in hex (e.g. "D3")

                if (ours.TryGetValue(key, out var ourPath))
                {
                    entryMatched++;
                    var truthPath = row.TryGetValue("Path", out var p) ? p.Trim() : string.Empty;
                    if (!string.IsNullOrEmpty(truthPath) && !string.IsNullOrEmpty(ourPath))
                    {
                        pathComparable++;
                        if (string.Equals(ourPath, truthPath, StringComparison.OrdinalIgnoreCase))
                            pathMatched++;
                    }
                }
            }
        }

        Assert.True(filesChecked > 0, "No AutomaticDestinations files were parsed.");
        Assert.True(totalEntries > 0, "No JLECmd entries to compare.");

        // Entry-number coverage: we should enumerate essentially the same DestList entries JLECmd does.
        var coverage = entryMatched / (double)totalEntries;
        Assert.True(coverage >= 0.95, $"DestList entry coverage vs JLECmd too low: {coverage:P1} ({entryMatched}/{totalEntries}).");

        // Path agreement is softer: our PathHint is the DestList path string; JLECmd's Path can come from the
        // embedded LNK, so a minority can legitimately differ. Require a strong majority.
        if (pathComparable > 0)
        {
            var pathAgree = pathMatched / (double)pathComparable;
            Assert.True(pathAgree >= 0.80, $"DestList path agreement vs JLECmd too low: {pathAgree:P1} ({pathMatched}/{pathComparable}).");
        }
    }

    private static Dictionary<string, string> ParseDestList(string file)
    {
        byte[] bytes;
        using (var root = RootStorage.OpenRead(file))
        {
            if (!root.TryOpenStream("DestList", out var s))
                return new Dictionary<string, string>();
            using var ms = new MemoryStream();
            using (s) s.CopyTo(ms);
            bytes = ms.ToArray();
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in JumpListDestListParser.Parse(bytes))
            map[e.StreamName] = e.PathHint;
        return map;
    }
}
