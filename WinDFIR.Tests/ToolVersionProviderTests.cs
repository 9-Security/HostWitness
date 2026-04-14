using WinDFIR.Core.Snapshot;
using WinDFIR.Providers;
using WinDFIR.UI;
using Xunit;

namespace WinDFIR.Tests;

public class ToolVersionProviderTests
{
    [Fact]
    public void GetCurrentVersion_ReturnsConsistentVersionAcrossReleaseAssemblies()
    {
        string uiVersion = ToolVersionProvider.GetCurrentVersion(typeof(MainWindow));
        string coreVersion = ToolVersionProvider.GetCurrentVersion(typeof(SnapshotExporter));
        string providerVersion = ToolVersionProvider.GetCurrentVersion(typeof(RegistrySearchProvider));

        Assert.False(string.IsNullOrWhiteSpace(uiVersion));
        Assert.Equal(uiVersion, coreVersion);
        Assert.Equal(uiVersion, providerVersion);
    }
}
