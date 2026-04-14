using System;
using System.Collections.Generic;
using WinDFIR.Providers;
using Xunit;

namespace WinDFIR.Tests;

public class ProcessKeyCacheTests
{
    [Fact]
    public void EventLogProvider_ResolveProcessKey_UsesCachedCreateTime()
    {
        var provider = new EventLogProvider();
        provider.ClearProcessCreateCacheForTest();

        var fields = new Dictionary<string, object>
        {
            ["CreationUtcTime"] = "2024-01-01T00:00:00Z"
        };

        var eventTime = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var first = provider.ResolveProcessKeyForTest(1234, eventTime, fields);
        var second = provider.ResolveProcessKeyForTest(1234, eventTime.AddSeconds(1), new Dictionary<string, object>());

        Assert.Equal(first.CreateTime, second.CreateTime);
    }

    [Fact]
    public void EtwProvider_ResolveProcessKey_UsesCachedCreateTime()
    {
        var provider = new ETWMonitorProvider();
        provider.ClearProcessCreateCacheForTest();

        var fields = new Dictionary<string, object>
        {
            ["ProcessStartTimeUtc"] = "2024-01-01T00:00:00Z"
        };

        var eventTime = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var first = provider.ResolveProcessKeyForTest(4321, eventTime, fields);
        var second = provider.ResolveProcessKeyForTest(4321, eventTime.AddSeconds(1), new Dictionary<string, object>());

        Assert.Equal(first.CreateTime, second.CreateTime);
    }
}
