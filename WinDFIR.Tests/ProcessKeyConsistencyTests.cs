using WinDFIR.Core.Entities;
using WinDFIR.Core.Normalization;
using Xunit;

namespace WinDFIR.Tests;

/// <summary>
/// ProcessKey generation consistency: same (bootId, pid, createTime) must yield equal ProcessKey.
/// </summary>
public class ProcessKeyConsistencyTests
{
    [Fact]
    public void SameBootIdPidCreateTime_ProducesEqualProcessKey()
    {
        var bootId = 12345UL;
        var pid = 9999U;
        var createTime = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        var key1 = KeyGenerator.GenerateProcessKey(bootId, pid, createTime);
        var key2 = KeyGenerator.GenerateProcessKey(bootId, pid, createTime);

        Assert.Equal(key1, key2);
        Assert.Equal(key1.BootId, key2.BootId);
        Assert.Equal(key1.ProcessId, key2.ProcessId);
        Assert.Equal(key1.CreateTime, key2.CreateTime);
    }

    [Fact]
    public void DifferentCreateTime_ProducesDifferentProcessKey()
    {
        var bootId = 1UL;
        var pid = 100U;
        var key1 = KeyGenerator.GenerateProcessKey(bootId, pid, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var key2 = KeyGenerator.GenerateProcessKey(bootId, pid, new DateTime(2024, 1, 1, 0, 0, 1, DateTimeKind.Utc));

        Assert.NotEqual(key1, key2);
        Assert.NotEqual(key1.CreateTime, key2.CreateTime);
    }
}
