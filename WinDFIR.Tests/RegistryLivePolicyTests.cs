using WinDFIR.Core.Settings;
using Xunit;

namespace WinDFIR.Tests;

public class RegistryLivePolicyTests
{
    [Fact]
    public void IsLiveRegistryEnabled_DefaultSettings_False()
    {
        Assert.False(RegistryLivePolicy.IsLiveRegistryEnabled(new HostWitnessSettings()));
    }

    [Fact]
    public void IsLiveRegistryEnabled_ForensicStrictStyle_False()
    {
        var s = new HostWitnessSettings
        {
            Ui = new UiSettings { RegistryUseOfflineOnly = true, EnableLiveRegistryExperimental = false }
        };
        Assert.False(RegistryLivePolicy.IsLiveRegistryEnabled(s));
    }

    [Fact]
    public void IsLiveRegistryEnabled_ExperimentalOnButOfflineOnlyStillTrue_False()
    {
        var s = new HostWitnessSettings
        {
            Ui = new UiSettings { RegistryUseOfflineOnly = true, EnableLiveRegistryExperimental = true }
        };
        Assert.False(RegistryLivePolicy.IsLiveRegistryEnabled(s));
    }

    [Fact]
    public void IsLiveRegistryEnabled_DualOptIn_True()
    {
        var s = new HostWitnessSettings
        {
            Ui = new UiSettings { RegistryUseOfflineOnly = false, EnableLiveRegistryExperimental = true }
        };
        Assert.True(RegistryLivePolicy.IsLiveRegistryEnabled(s));
    }

    [Fact]
    public void IsLiveRegistryEnabled_OfflineOffButExperimentalOff_False()
    {
        var s = new HostWitnessSettings
        {
            Ui = new UiSettings { RegistryUseOfflineOnly = false, EnableLiveRegistryExperimental = false }
        };
        Assert.False(RegistryLivePolicy.IsLiveRegistryEnabled(s));
    }
}
