using System;
using System.Collections.Generic;
using System.Linq;
using WinDFIR.Providers;
using Xunit;

namespace WinDFIR.Tests;

public class OfflineHiveRegistryProviderIntegrationTests
{
    private static Dictionary<string, object> BaseFields(bool vss, string consistency, DateTime? snapshotUtc)
    {
        var d = new Dictionary<string, object>
        {
            ["HiveName"] = "SYSTEM",
            ["HivePath"] = @"C:\Windows\System32\config\SYSTEM",
            ["SnapshotPath"] = @"C:\snap\SYSTEM",
            ["KeyPath"] = @"ControlSet001\Control\Session Manager\AppCompatCache",
            ["QueryName"] = "AppCompatCache",
            ["Mode"] = "Offline",
            ["Parser"] = "Registry",
            ["OfflineHiveSource"] = vss ? "VSS" : "Live",
            ["ConsistencyScope"] = consistency
        };
        if (snapshotUtc.HasValue)
            d["SnapshotTimeUtc"] = snapshotUtc.Value;
        return d;
    }

    [Fact]
    public void AppCompatCache_EmitsRawFirst_ThenDecodedRows_WithSharedSemantics()
    {
        var blob = new byte[128 + 552 * 2];
        WriteWin7Path(blob, 128, @"C:\Windows\a.exe");
        WriteWin7Ft(blob, 128 + 520, new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteWin7Path(blob, 128 + 552, @"C:\Windows\b.exe");
        WriteWin7Ft(blob, 128 + 552 + 520, new DateTime(2018, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var snap = new DateTime(2019, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var baseFields = BaseFields(true, "SingleSnapshot", snap);
        var hex = BitConverter.ToString(blob).Replace("-", " ");
        var display = hex.Length > 200 ? hex[..200] + "..." : hex;

        var events = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            baseFields,
            @"SYSTEM\ControlSet001\Control\Session Manager\AppCompatCache",
            new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "AppCompatCache",
            @"ControlSet001\Control\Session Manager\AppCompatCache",
            "AppCompatCache",
            "Binary",
            display,
            blob,
            @"C:\snap\SYSTEM");

        Assert.True(events.Count >= 3);
        var raw = events[0];
        Assert.Equal("AppCompatCache", raw.Fields["QueryName"]);
        Assert.False(raw.Fields.ContainsKey("OfflineHiveDecoded"));
        var rawData = raw.Fields["ValueData"]?.ToString() ?? string.Empty;
        Assert.True(rawData.Length > 50);
        Assert.Contains("00", rawData, StringComparison.Ordinal);

        foreach (var e in events.Skip(1))
        {
            Assert.Equal("ShimCache", e.Fields["OfflineHiveDecoded"]);
            Assert.Equal("VSS", e.Fields["OfflineHiveSource"]);
            Assert.Equal("SingleSnapshot", e.Fields["ConsistencyScope"]);
            Assert.Equal(snap, e.Fields["SnapshotTimeUtc"]);
            Assert.Equal("ControlSet001", e.Fields["ShimCache_ControlSet"]);
        }
    }

    [Fact]
    public void AppCompatCache_NoDecode_OnlyRaw_WhenBlobUnparsed()
    {
        var garbage = new byte[120];
        Random.Shared.NextBytes(garbage);
        var display = BitConverter.ToString(garbage).Replace("-", " ");
        var events = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            BaseFields(false, "SingleLive", null),
            @"SYSTEM\key",
            DateTime.UtcNow,
            "AppCompatCache",
            @"ControlSet001\Control\Session Manager\AppCompatCache",
            "AppCompatCache",
            "Binary",
            display,
            garbage,
            @"C:\x\SYSTEM");

        Assert.Single(events);
        Assert.False(events[0].Fields.ContainsKey("OfflineHiveDecoded"));
    }

    [Fact]
    public void AppCompatCache_DecodeException_StillLeavesRaw()
    {
        var blob = new byte[64];
        var events = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            BaseFields(false, "SingleLive", null),
            @"SYSTEM\key",
            DateTime.UtcNow,
            "AppCompatCache",
            @"ControlSet001\Control\Session Manager\AppCompatCache",
            "AppCompatCache",
            "Binary",
            "AA",
            blob,
            @"C:\x\SYSTEM",
            _ => throw new InvalidOperationException("simulated"));

        Assert.Single(events);
        Assert.False(events[0].Fields.ContainsKey("OfflineHiveDecoded"));
    }

    [Fact]
    public void UserAssist_DecodeSuccess_AddsStructuredFields_SingleEvent()
    {
        var ft = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var data = new byte[16];
        BitConverter.GetBytes(1u).CopyTo(data, 0);
        BitConverter.GetBytes(9u).CopyTo(data, 4);
        BitConverter.GetBytes((ulong)ft).CopyTo(data, 8);

        var events = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            BaseFields(false, "SingleLive", null),
            @"NTUSER.DAT\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{guid}\Count",
            DateTime.UtcNow,
            "UserAssist",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{guid}\Count",
            "UEME_RUNPIDL:Z:\\foo.exe",
            "Binary",
            BitConverter.ToString(data).Replace("-", " "),
            data,
            @"C:\ntuser.dat");

        Assert.Single(events);
        Assert.Equal("UserAssist", events[0].Fields["OfflineHiveDecoded"]);
        Assert.Equal(9u, events[0].Fields["UserAssist_RunCount"]);
        Assert.True(events[0].Fields.ContainsKey("UserAssist_DecodedName"));
    }

    [Fact]
    public void UserAssist_DecodeFail_RawOnly_NoOfflineHiveDecoded()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7 }; // < 8 bytes
        var events = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            BaseFields(false, "SingleLive", null),
            @"NTUSER.DAT\...\Count",
            DateTime.UtcNow,
            "UserAssist",
            @"Software\...\UserAssist\{g}\Count",
            "SomeName",
            "Binary",
            BitConverter.ToString(data).Replace("-", " "),
            data,
            @"C:\ntuser.dat");

        Assert.Single(events);
        Assert.False(events[0].Fields.ContainsKey("OfflineHiveDecoded"));
    }

    private static void WriteWin7Path(byte[] blob, int offset, string path)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(path + "\0");
        Array.Clear(blob, offset, 520);
        Array.Copy(bytes, 0, blob, offset, Math.Min(bytes.Length, 520));
    }

    private static void WriteWin7Ft(byte[] blob, int offset, DateTime utc)
    {
        BitConverter.GetBytes((ulong)utc.ToFileTimeUtc()).CopyTo(blob, offset);
    }
}
