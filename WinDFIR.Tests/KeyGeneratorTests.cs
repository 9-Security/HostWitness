using WinDFIR.Core.Entities;
using WinDFIR.Core.Normalization;
using Xunit;

namespace WinDFIR.Tests;

public class KeyGeneratorTests
{
    [Fact]
    public void GenerateProcessKey_WithValidInputs_ReturnsProcessKey()
    {
        // Arrange
        var bootId = 12345UL;
        var processId = 5678U;
        var createTime = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var key = KeyGenerator.GenerateProcessKey(bootId, processId, createTime);

        // Assert
        Assert.Equal(bootId, key.BootId);
        Assert.Equal(processId, key.ProcessId);
        Assert.Equal(createTime, key.CreateTime);
    }

    [Fact]
    public void GenerateNetworkFlowKey_WithValidInputs_ReturnsNetworkFlowKey()
    {
        // Arrange
        var protocol = "TCP";
        var localEndpoint = "192.168.1.100:8080";
        var remoteEndpoint = "10.0.0.1:443";
        var processId = 1234U;
        var timeBucket = DateTime.UtcNow;

        // Act
        var key = KeyGenerator.GenerateNetworkFlowKey(protocol, localEndpoint, remoteEndpoint, processId, timeBucket);

        // Assert
        Assert.Equal(protocol, key.Protocol);
        Assert.Equal(localEndpoint, key.LocalEndpoint);
        Assert.Equal(remoteEndpoint, key.RemoteEndpoint);
        Assert.Equal(processId, key.ProcessId);
        Assert.Equal(timeBucket, key.TimeBucket);
    }

    /// <summary>
    /// Primary path for network identity: same logical connection as legacy <see cref="KeyGenerator.GenerateNetworkKey"/> expressed as endpoint strings.
    /// </summary>
    [Fact]
    public void GenerateNetworkFlowKey_FromHostPortParts_MatchesLegacyNetworkKeyShape()
    {
        var protocol = "TCP";
        var localAddress = "192.168.1.100";
        var localPort = 8080;
        var remoteAddress = "10.0.0.1";
        var remotePort = 443;
        var timeBucket = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var flow = KeyGenerator.GenerateNetworkFlowKey(
            protocol,
            $"{localAddress}:{localPort}",
            $"{remoteAddress}:{remotePort}",
            processId: null,
            timeBucket);

        Assert.Equal(protocol, flow.Protocol);
        Assert.Equal($"{localAddress}:{localPort}", flow.LocalEndpoint);
        Assert.Equal($"{remoteAddress}:{remotePort}", flow.RemoteEndpoint);
        Assert.Null(flow.ProcessId);
        Assert.Equal(timeBucket, flow.TimeBucket);
    }

    [Fact]
    public void GenerateUserKey_WithValidSid_ReturnsUserKey()
    {
        // Arrange
        var sid = "S-1-5-21-1234567890-1234567890-1234567890-1001";

        // Act
        var key = KeyGenerator.GenerateUserKey(sid);

        // Assert
        Assert.Equal(sid, key.Sid);
    }

    [Fact]
    public void GenerateFileKey_WithVolumeSerialAndFileId_ReturnsFileKey()
    {
        // Arrange
        var volumeSerial = "12345678";
        var fileId = 9876543210UL;

        // Act
        var key = KeyGenerator.GenerateFileKey(volumeSerial, fileId, null, null);

        // Assert
        Assert.Equal(volumeSerial, key.VolumeSerial);
        Assert.Equal(fileId, key.FileId);
    }

    [Fact]
    public void GenerateRegistryKey_WithNormalizedPath_ReturnsRegistryKey()
    {
        // Arrange
        var normalizedPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion";

        // Act
        var key = KeyGenerator.GenerateRegistryKey(normalizedPath);

        // Assert
        Assert.Equal(normalizedPath, key.NormalizedPath);
    }

    [Fact]
    public void GenerateHostKey_WithMachineSidAndHostname_ReturnsHostKey()
    {
        // Arrange
        var machineSid = "S-1-5-21-1234567890-1234567890-1234567890";
        var hostname = "WORKSTATION01";

        // Act
        var key = KeyGenerator.GenerateHostKey(machineSid, hostname);

        // Assert
        Assert.Equal(machineSid, key.MachineSid);
        Assert.Equal(hostname, key.Hostname);
    }

    /// <summary>Compatibility coverage for obsolete <see cref="KeyGenerator.GenerateNetworkKey"/> / <see cref="NetworkKey"/> until migration completes.</summary>
    [Fact]
    public void GenerateNetworkKey_LegacyObsoleteApi_StillPopulatesFields()
    {
        var localAddress = "192.168.1.100";
        var localPort = 8080;
        var remoteAddress = "10.0.0.1";
        var remotePort = 443;
        var protocol = "TCP";

#pragma warning disable CS0618 // GenerateNetworkKey is obsolete; intentional legacy test
        var key = KeyGenerator.GenerateNetworkKey(localAddress, (ushort)localPort, remoteAddress, (ushort)remotePort, protocol);
#pragma warning restore CS0618

        Assert.Equal(localAddress, key.LocalAddress);
        Assert.Equal(localPort, key.LocalPort);
        Assert.Equal(remoteAddress, key.RemoteAddress);
        Assert.Equal(remotePort, key.RemotePort);
        Assert.Equal(protocol, key.Protocol);
    }
}
