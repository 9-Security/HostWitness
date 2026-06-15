using WinDFIR.Providers;
using Xunit;

namespace WinDFIR.Tests;

public class EventLogOfflineEvtxTests
{
    [Theory]
    [InlineData(@"C:\evidence\Security.evtx", "Security")]
    [InlineData(@"Application.evtx", "Application")]
    // Exported operational channels encode '/' as '%4'.
    [InlineData(@"D:\case\Microsoft-Windows-PowerShell%4Operational.evtx", "Microsoft-Windows-PowerShell/Operational")]
    [InlineData(@"X:\x\Microsoft-Windows-Windows Defender%4Operational.evtx", "Microsoft-Windows-Windows Defender/Operational")]
    public void DeriveLogNameFromEvtxPath_DecodesChannelName(string path, string expected)
    {
        Assert.Equal(expected, EventLogProvider.DeriveLogNameFromEvtxPath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DeriveLogNameFromEvtxPath_FallsBackForBlank(string? path)
    {
        Assert.Equal("OfflineEvtx", EventLogProvider.DeriveLogNameFromEvtxPath(path));
    }

    [Fact]
    public void NewProvider_IsLiveByDefault()
    {
        var provider = new EventLogProvider();
        Assert.False(provider.IsOfflineMode);
        Assert.Empty(provider.EvtxFilePaths);
    }

    [Fact]
    public void AddEvtxFile_SwitchesToOfflineMode()
    {
        var provider = new EventLogProvider();
        provider.AddEvtxFile(@"C:\evidence\Security.evtx");

        Assert.True(provider.IsOfflineMode);
        Assert.Single(provider.EvtxFilePaths);
        Assert.Equal(@"C:\evidence\Security.evtx", provider.EvtxFilePaths[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddEvtxFile_IgnoresBlankPaths(string? path)
    {
        var provider = new EventLogProvider();
        provider.AddEvtxFile(path!);

        Assert.False(provider.IsOfflineMode);
        Assert.Empty(provider.EvtxFilePaths);
    }
}
