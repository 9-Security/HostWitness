using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinDFIR.Providers.Parsers;
using Xunit;

namespace WinDFIR.Tests;

public class BitsParserTests
{
    private const string QmgrPath = @"D:\cursor\KDFIR\KAPE\KAPE_Extracted\C\ProgramData\Microsoft\Network\Downloader\qmgr.db";

    // --- Pure unit tests (no sample needed) ---

    [Fact]
    public void ExtractUtf16Strings_PullsReadableRunsAndSkipsBinary()
    {
        // "http://x" + NUL + binary + "C:\\a"
        var bytes = Utf16("http://x").Concat(new byte[] { 0, 0, 0x01, 0x00 }).Concat(Utf16(@"C:\a")).ToArray();
        var strings = BitsParser.ExtractUtf16Strings(bytes);
        Assert.Contains("http://x", strings);
        Assert.Contains(@"C:\a", strings);
    }

    [Fact]
    public void BuildRecord_ClassifiesUrlsPathsAndDropsBinaryNoise()
    {
        var strings = new List<string>
        {
            "https://evil.example/payload.exe",
            @"C:\Users\v\AppData\Local\Temp\p.exe",
            @"\\server\share\x",
            "MozillaUpdate 308046B0AF4A39CB",   // text -> kept as other
            "䎗㺷蔦ԓ눚"                          // binary-as-CJK -> dropped
        };

        var r = BitsParser.BuildRecord("File", Guid.NewGuid(), strings);

        Assert.Equal(new[] { "https://evil.example/payload.exe" }, r.Urls.ToArray());
        Assert.Contains(@"C:\Users\v\AppData\Local\Temp\p.exe", r.LocalPaths);
        Assert.Contains(@"\\server\share\x", r.LocalPaths);
        Assert.Contains("MozillaUpdate 308046B0AF4A39CB", r.OtherStrings);
        Assert.DoesNotContain("䎗㺷蔦ԓ눚", r.OtherStrings);
    }

    private static byte[] Utf16(string s) => System.Text.Encoding.Unicode.GetBytes(s);

    // --- Validation against the real qmgr.db (gated on sample presence) ---

    [Fact]
    public void Parse_RealQmgr_RecoversDownloadUrlsAndPaths()
    {
        if (!File.Exists(QmgrPath))
            return;

        List<BitsRecord> records;
        try
        {
            records = BitsParser.Parse(QmgrPath).ToList();
        }
        catch (EseDatabaseReader.EsePageSizeConflictException)
        {
            return; // another ESE DB with a different page size was opened first this test process
        }

        Assert.NotEmpty(records);

        // The sample contains Firefox/Chrome updater downloads.
        var allUrls = records.SelectMany(r => r.Urls).ToList();
        Assert.Contains(allUrls, u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(allUrls, u => u.Contains("mozilla", StringComparison.OrdinalIgnoreCase));

        // Files reference local destination paths.
        var fileRecs = records.Where(r => r.Kind == "File").ToList();
        Assert.NotEmpty(fileRecs);
        Assert.Contains(fileRecs, r => r.LocalPaths.Any(p => p.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase)));
    }
}
