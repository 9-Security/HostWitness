using System;
using System.Collections.Generic;
using WinDFIR.Providers;
using WinDFIR.Providers.Parsers;
using Xunit;

namespace WinDFIR.Tests;

public class BamDamTests
{
    [Theory]
    [InlineData(@"ControlSet001\Services\bam\State\UserSettings\S-1-5-21-1-2-3-1001", true, "BAM")]
    [InlineData(@"ControlSet001\Services\dam\State\UserSettings\S-1-5-18", true, "DAM")]
    [InlineData(@"ControlSet001\Services\bam\UserSettings\S-1-5-21-9-9-9-500", true, "BAM")] // older layout (no State)
    [InlineData(@"ControlSet001\Services\bam\State\UserSettings", false, "")]               // no SID leaf
    [InlineData(@"ControlSet001\Services\wuauserv", false, "")]
    [InlineData(@"", false, "")]
    public void IsBamUserSettingsKey_DetectsPerUserKeys(string keyPath, bool expected, string expectedComponent)
    {
        var result = BamDamParser.IsBamUserSettingsKey(keyPath, out var component);
        Assert.Equal(expected, result);
        Assert.Equal(expectedComponent, component);
    }

    [Fact]
    public void TryDecodeLastExecution_ReadsFiletimeFromExecutableEntry()
    {
        var expected = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var raw = BitConverter.GetBytes(expected.ToFileTimeUtc()); // 8-byte LE FILETIME

        var ok = BamDamParser.TryDecodeLastExecution(
            @"\Device\HarddiskVolume3\Windows\System32\cmd.exe", raw, out var decoded);

        Assert.True(ok);
        Assert.Equal(expected, decoded);
    }

    [Theory]
    [InlineData("Version")]          // bookkeeping value (no path) — not an executable entry
    [InlineData("SequenceNumber")]
    public void TryDecodeLastExecution_IgnoresNonExecutableValues(string valueName)
    {
        var raw = BitConverter.GetBytes(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc());
        Assert.False(BamDamParser.TryDecodeLastExecution(valueName, raw, out _));
    }

    [Fact]
    public void TryDecodeLastExecution_RejectsShortOrZeroOrImplausible()
    {
        Assert.False(BamDamParser.TryDecodeLastExecution(@"\Device\x\a.exe", new byte[] { 1, 2, 3 }, out _));
        Assert.False(BamDamParser.TryDecodeLastExecution(@"\Device\x\a.exe", new byte[8], out _)); // all zero
        var year1601 = BitConverter.GetBytes(1L); // FILETIME=1 -> year 1601, out of bounds
        Assert.False(BamDamParser.TryDecodeLastExecution(@"\Device\x\a.exe", year1601, out _));
    }

    [Fact]
    public void OfflineHive_DecodesBamEntry_AndAnchorsTimestampAtExecution()
    {
        var exec = new DateTime(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc);
        var raw = BitConverter.GetBytes(exec.ToFileTimeUtc());
        var keyLastWrite = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); // different from exec time

        var baseFields = new Dictionary<string, object>
        {
            ["HiveName"] = "SYSTEM",
            ["QueryName"] = "Services",
            ["Mode"] = "Offline"
        };

        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            baseFields,
            @"SYSTEM\ControlSet001\Services\bam\State\UserSettings\S-1-5-21-1-2-3-1001",
            keyLastWrite,
            "Services",
            @"ControlSet001\Services\bam\State\UserSettings\S-1-5-21-1-2-3-1001",
            @"\Device\HarddiskVolume3\Windows\System32\powershell.exe",
            "REG_BINARY",
            "AA BB CC",
            raw,
            @"X:\SYSTEM");

        var e = Assert.Single(evts);
        Assert.Equal("BAM", e.Fields["OfflineHiveDecoded"]);
        Assert.Equal("BAM", e.Fields["BamComponent"]);
        Assert.Equal("S-1-5-21-1-2-3-1001", e.Fields["BamUserSid"]);
        Assert.Equal(@"\Device\HarddiskVolume3\Windows\System32\powershell.exe", e.Fields["BamExecutablePath"]);
        Assert.Equal(exec, e.Timestamp); // anchored at execution time, not key last-write
        Assert.Contains("BAM last-exec", e.Summary ?? "");
    }

    [Fact]
    public void OfflineHive_NonBamServicesValue_NotDecodedAsBam()
    {
        var baseFields = new Dictionary<string, object> { ["QueryName"] = "Services" };
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            baseFields,
            @"SYSTEM\ControlSet001\Services\wuauserv",
            DateTime.UtcNow,
            "Services",
            @"ControlSet001\Services\wuauserv",
            "ImagePath",
            "REG_SZ",
            @"C:\Windows\system32\svchost.exe",
            null,
            @"X:\SYSTEM");

        var e = Assert.Single(evts);
        Assert.False(e.Fields.ContainsKey("BamExecutablePath"));
    }
}
