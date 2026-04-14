using System;
using System.Collections.Generic;
using WinDFIR.Providers;
using Xunit;

namespace WinDFIR.Tests;

public class OfflineHivePhase3PersistenceTests
{
    private static Dictionary<string, object> BaseFields()
    {
        return new Dictionary<string, object>
        {
            ["HiveName"] = "SOFTWARE",
            ["HivePath"] = @"X:\SOFTWARE",
            ["SnapshotPath"] = @"X:\SOFTWARE",
            ["KeyPath"] = "",
            ["QueryName"] = "",
            ["Mode"] = "Offline",
            ["Parser"] = "Registry",
            ["OfflineHiveSource"] = "VSS",
            ["ConsistencyScope"] = "SingleSnapshot",
            ["SnapshotTimeUtc"] = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        };
    }

    [Fact]
    public void Services_StructuredFields_ImagePath_ControlSet_Preserved()
    {
        var bf = BaseFields();
        bf["QueryName"] = "Services";
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            bf,
            @"SYSTEM\ControlSet001\Services\wuauserv",
            DateTime.UtcNow,
            "Services",
            @"ControlSet001\Services\wuauserv",
            "ImagePath",
            "REG_SZ",
            @"C:\Windows\system32\svchost.exe -k netsvcs",
            null,
            @"X:\SYSTEM");

        var e = Assert.Single(evts);
        Assert.Equal("Services", e.Fields["OfflineHiveDecoded"]);
        Assert.Equal("wuauserv", e.Fields["ServiceName"]);
        Assert.Equal("ControlSet001", e.Fields["ServiceControlSet"]);
        Assert.Contains("svchost", e.Fields["ServiceImagePath"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal("VSS", e.Fields["OfflineHiveSource"]);
        Assert.Equal("SingleSnapshot", e.Fields["ConsistencyScope"]);
        Assert.Equal(bf["SnapshotTimeUtc"], e.Fields["SnapshotTimeUtc"]);
        Assert.Contains("svchost", e.Fields["ValueData"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Services_Parameters_ServiceDll_Enriched()
    {
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            BaseFields(),
            @"SYSTEM\ControlSet001\Services\SomeSvc\Parameters",
            DateTime.UtcNow,
            "Services",
            @"ControlSet001\Services\SomeSvc\Parameters",
            "ServiceDll",
            "REG_SZ",
            @"C:\Windows\foo.dll",
            null,
            @"X:\SYSTEM");

        var e = Assert.Single(evts);
        Assert.Equal("SomeSvc", e.Fields["ServiceName"]);
        Assert.Equal(@"C:\Windows\foo.dll", e.Fields["ServiceDll"]);
    }

    [Fact]
    public void Services_UnknownValue_NoOfflineHiveDecoded_RawOnly()
    {
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            BaseFields(),
            @"SYSTEM\ControlSet001\Services\abc",
            DateTime.UtcNow,
            "Services",
            @"ControlSet001\Services\abc",
            "DependOnService",
            "REG_MULTI_SZ",
            "other",
            null,
            @"X:\SYSTEM");

        var e = Assert.Single(evts);
        Assert.False(e.Fields.ContainsKey("OfflineHiveDecoded"));
    }

    [Fact]
    public void Services_Description_MapsToServiceDescription_NotDisplayName()
    {
        var bf = BaseFields();
        bf["QueryName"] = "Services";
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            bf,
            @"SYSTEM\ControlSet001\Services\MySvc",
            DateTime.UtcNow,
            "Services",
            @"ControlSet001\Services\MySvc",
            "Description",
            "REG_SZ",
            "Built-in service description text",
            null,
            @"X:\SYSTEM");

        var e = Assert.Single(evts);
        Assert.Equal("Services", e.Fields["OfflineHiveDecoded"]);
        Assert.Equal("Built-in service description text", e.Fields["ServiceDescription"]?.ToString());
        Assert.False(e.Fields.ContainsKey("ServiceDisplayName"));
        Assert.Equal("VSS", e.Fields["OfflineHiveSource"]);
        Assert.Equal("SingleSnapshot", e.Fields["ConsistencyScope"]);
        Assert.Equal(bf["SnapshotTimeUtc"], e.Fields["SnapshotTimeUtc"]);
        Assert.Equal("Built-in service description text", e.Fields["ValueData"]?.ToString());
    }

    [Fact]
    public void Services_FailureCommand_MapsToServiceFailureCommand()
    {
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            BaseFields(),
            @"SYSTEM\ControlSet001\Services\MySvc",
            DateTime.UtcNow,
            "Services",
            @"ControlSet001\Services\MySvc",
            "FailureCommand",
            "REG_EXPAND_SZ",
            @"C:\Windows\System32\cmd.exe /c recovery.bat",
            null,
            @"X:\SYSTEM");

        var e = Assert.Single(evts);
        Assert.Contains("recovery.bat", e.Fields["ServiceFailureCommand"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.False(e.Fields.ContainsKey("ServiceRebootMessage"));
    }

    [Fact]
    public void Services_RebootMessage_MapsToServiceRebootMessage_NotFailureCommand()
    {
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            BaseFields(),
            @"SYSTEM\ControlSet001\Services\MySvc",
            DateTime.UtcNow,
            "Services",
            @"ControlSet001\Services\MySvc",
            "RebootMessage",
            "REG_SZ",
            "System will restart after failure",
            null,
            @"X:\SYSTEM");

        var e = Assert.Single(evts);
        Assert.Equal("System will restart after failure", e.Fields["ServiceRebootMessage"]?.ToString());
        Assert.False(e.Fields.ContainsKey("ServiceFailureCommand"));
        Assert.Equal("Services", e.Fields["OfflineHiveDecoded"]);
    }

    [Fact]
    public void StartupApproved_DecodesState_AndPreservesRaw()
    {
        var raw = new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var display = BitConverter.ToString(raw).Replace("-", " ");
        var bf = BaseFields();
        bf["QueryName"] = "StartupApprovedRun";
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            bf,
            @"SOFTWARE\...\StartupApproved\Run",
            DateTime.UtcNow,
            "StartupApprovedRun",
            @"Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            "deadbeef",
            "REG_BINARY",
            display,
            raw,
            @"X:\SOFTWARE");

        var e = Assert.Single(evts);
        Assert.Equal("StartupApprovedRun", e.Fields["OfflineHiveDecoded"]);
        Assert.Equal("Disabled", e.Fields["StartupApproved_State"]);
        Assert.Equal("deadbeef", e.Fields["StartupApproved_EntryName"]);
        Assert.True(e.Fields.ContainsKey("ValueData"));
    }

    [Fact]
    public void StartupApproved_EnabledByte_Classified()
    {
        var raw = new byte[] { 0x03, 0x01 };
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            BaseFields(),
            @"SOFTWARE\k",
            DateTime.UtcNow,
            "StartupApprovedRun",
            @"Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            "x",
            "REG_BINARY",
            BitConverter.ToString(raw).Replace("-", " "),
            raw,
            @"X:\SOFTWARE");

        Assert.Equal("Enabled", Assert.Single(evts).Fields["StartupApproved_State"]);
    }

    [Fact]
    public void IFEO_Debugger_Structured_ConservativeSummary()
    {
        var bf = BaseFields();
        bf["QueryName"] = "IFEO";
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            bf,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\notepad.exe",
            DateTime.UtcNow,
            "IFEO",
            @"Microsoft\Windows NT\CurrentVersion\Image File Execution Options\notepad.exe",
            "Debugger",
            "REG_SZ",
            @"C:\dbg\cdb.exe",
            null,
            @"X:\SOFTWARE");

        var e = Assert.Single(evts);
        Assert.Equal("IFEO", e.Fields["OfflineHiveDecoded"]);
        Assert.Equal("notepad.exe", e.Fields["IFEO_TargetImage"]);
        Assert.Contains("cdb.exe", e.Fields["IFEO_Debugger"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IFEO", e.Summary ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal("VSS", e.Fields["OfflineHiveSource"]);
    }

    [Fact]
    public void IFEO_UnknownValue_NotEnriched()
    {
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            BaseFields(),
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\calc.exe",
            DateTime.UtcNow,
            "IFEO",
            @"Microsoft\Windows NT\CurrentVersion\Image File Execution Options\calc.exe",
            "UseFilter",
            "REG_DWORD",
            "1",
            null,
            @"X:\SOFTWARE");

        Assert.False(Assert.Single(evts).Fields.ContainsKey("OfflineHiveDecoded"));
    }

    [Fact]
    public void Winlogon_Shell_Structured()
    {
        var bf = BaseFields();
        bf["QueryName"] = "Winlogon";
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            bf,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
            DateTime.UtcNow,
            "Winlogon",
            @"Microsoft\Windows NT\CurrentVersion\Winlogon",
            "Shell",
            "REG_SZ",
            "explorer.exe",
            null,
            @"X:\SOFTWARE");

        var e = Assert.Single(evts);
        Assert.Equal("Winlogon", e.Fields["OfflineHiveDecoded"]);
        Assert.Equal("explorer.exe", e.Fields["Winlogon_Shell"]);
    }

    [Fact]
    public void Winlogon_Userinit_Structured()
    {
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            BaseFields(),
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
            DateTime.UtcNow,
            "Winlogon",
            @"Microsoft\Windows NT\CurrentVersion\Winlogon",
            "Userinit",
            "REG_SZ",
            @"C:\Windows\system32\userinit.exe,",
            null,
            @"X:\SOFTWARE");

        Assert.Contains("userinit", Assert.Single(evts).Fields["Winlogon_Userinit"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserAssist_Decoding_Unchanged_ByPhase3EnrichmentOrder()
    {
        var ft = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var data = new byte[16];
        BitConverter.GetBytes(1u).CopyTo(data, 0);
        BitConverter.GetBytes(2u).CopyTo(data, 4);
        BitConverter.GetBytes((ulong)ft).CopyTo(data, 8);

        var bf = BaseFields();
        bf["QueryName"] = "UserAssist";
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            bf,
            @"NTUSER.DAT\Software\...\UserAssist\{g}\Count",
            DateTime.UtcNow,
            "UserAssist",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{g}\Count",
            "UEME_RUNPIDL:Z:\\x.exe",
            "REG_BINARY",
            BitConverter.ToString(data).Replace("-", " "),
            data,
            @"X:\NTUSER.DAT");

        var e = Assert.Single(evts);
        Assert.Equal("UserAssist", e.Fields["OfflineHiveDecoded"]);
        Assert.Equal(2u, e.Fields["UserAssist_RunCount"]);
    }
}
