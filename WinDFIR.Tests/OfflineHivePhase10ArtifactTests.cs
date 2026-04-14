using System;
using System.Collections.Generic;
using WinDFIR.Providers;
using Xunit;

namespace WinDFIR.Tests;

public class OfflineHivePhase10ArtifactTests
{
    private static Dictionary<string, object> BaseSoftwareFields()
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

    private static Dictionary<string, object> BaseSystemFields()
    {
        return new Dictionary<string, object>
        {
            ["HiveName"] = "SYSTEM",
            ["HivePath"] = @"X:\SYSTEM",
            ["SnapshotPath"] = @"X:\SYSTEM",
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
    public void BitsClient_StructuredFields_AndPreservesMetadata()
    {
        var bf = BaseSoftwareFields();
        bf["QueryName"] = "BitsClient";
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            bf,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\BITS\StateIndex\{guid}",
            DateTime.UtcNow,
            "BitsClient",
            @"Microsoft\Windows\CurrentVersion\BITS\StateIndex\{guid}",
            "JobPriority",
            "REG_DWORD",
            "1",
            null,
            @"X:\SOFTWARE");

        var e = Assert.Single(evts);
        Assert.Equal("BITS", e.Fields["OfflineArtifactFamily"]);
        Assert.Equal("BITS_Registry", e.Fields["OfflineHiveDecoded"]);
        Assert.Equal("1", e.Fields["Bits_ValuePreview"]?.ToString());
        Assert.Contains("does not prove", e.Fields["Bits_Note"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal("VSS", e.Fields["OfflineHiveSource"]);
        Assert.Equal("SingleSnapshot", e.Fields["ConsistencyScope"]);
        Assert.Equal(bf["SnapshotTimeUtc"], e.Fields["SnapshotTimeUtc"]);
    }

    [Fact]
    public void WmiCimom_StructuredFields_ConservativeNote()
    {
        var bf = BaseSoftwareFields();
        bf["QueryName"] = "WmiCimom";
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            bf,
            @"SOFTWARE\Microsoft\WBEM\CIMOM",
            DateTime.UtcNow,
            "WmiCimom",
            @"Microsoft\WBEM\CIMOM",
            "Logging",
            "REG_DWORD",
            "1",
            null,
            @"X:\SOFTWARE");

        var e = Assert.Single(evts);
        Assert.Equal("WMI", e.Fields["OfflineArtifactFamily"]);
        Assert.Equal("WMI_Cimom", e.Fields["OfflineHiveDecoded"]);
        Assert.Equal("Logging", e.Fields["Wmi_ValueName"]);
        Assert.Contains("__EventFilter", e.Fields["Wmi_Note"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WmiCimom_EmptyValueName_NotEnriched()
    {
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            BaseSoftwareFields(),
            @"SOFTWARE\Microsoft\WBEM\CIMOM",
            DateTime.UtcNow,
            "WmiCimom",
            @"Microsoft\WBEM\CIMOM",
            "",
            "REG_SZ",
            "x",
            null,
            @"X:\SOFTWARE");

        Assert.False(Assert.Single(evts).Fields.ContainsKey("OfflineHiveDecoded"));
    }

    [Fact]
    public void WmiNamespaceSecurity_Structured_LastSegmentAsNamespaceKey()
    {
        var bf = BaseSystemFields();
        bf["QueryName"] = "WmiNamespaceSecurity";
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            bf,
            @"SYSTEM\ControlSet001\Control\WMI\Security\Root",
            DateTime.UtcNow,
            "WmiNamespaceSecurity",
            @"ControlSet001\Control\WMI\Security\Root",
            "Default",
            "REG_BINARY",
            "01 02",
            new byte[] { 1, 2 },
            @"X:\SYSTEM");

        var e = Assert.Single(evts);
        Assert.Equal("WMI", e.Fields["OfflineArtifactFamily"]);
        Assert.Equal("WMI_NamespaceSecurity", e.Fields["OfflineHiveDecoded"]);
        Assert.Equal("Root", e.Fields["Wmi_SecurityNamespaceKey"]);
        Assert.Equal("Default", e.Fields["Wmi_ValueName"]);
    }

    [Fact]
    public void WmiNamespaceSecurity_EmptyRelativeKey_NotEnriched()
    {
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            BaseSystemFields(),
            @"SYSTEM\",
            DateTime.UtcNow,
            "WmiNamespaceSecurity",
            "",
            "x",
            "REG_SZ",
            "y",
            null,
            @"X:\SYSTEM");

        Assert.False(Assert.Single(evts).Fields.ContainsKey("OfflineHiveDecoded"));
    }

    [Fact]
    public void SrumRegistry_Structured_NoEseClaim()
    {
        var bf = BaseSoftwareFields();
        bf["QueryName"] = "SrumRegistry";
        var evts = OfflineHiveRegistryProvider.BuildOfflineRegistryValueEventsForTest(
            bf,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SRUM\Foo",
            DateTime.UtcNow,
            "SrumRegistry",
            @"Microsoft\Windows NT\CurrentVersion\SRUM\Foo",
            "Bar",
            "REG_SZ",
            "baz",
            null,
            @"X:\SOFTWARE");

        var e = Assert.Single(evts);
        Assert.Equal("SRUM", e.Fields["OfflineArtifactFamily"]);
        Assert.Equal("SRUM_Registry", e.Fields["OfflineHiveDecoded"]);
        Assert.Contains("SRUDB", e.Fields["Srum_Note"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not decoded", e.Fields["Srum_Note"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
