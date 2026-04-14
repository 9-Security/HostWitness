using WinDFIR.Providers;
using Xunit;

namespace WinDFIR.Tests;

public class BootIdProviderTests
{
    [Fact]
    public void BootId_IsStableWithinProcess()
    {
        var first = BootIdProvider.GetBootId();
        var second = BootIdProvider.GetBootId();

        Assert.Equal(first, second);
    }
}
