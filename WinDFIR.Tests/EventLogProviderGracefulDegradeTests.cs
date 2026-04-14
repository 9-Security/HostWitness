using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using WinDFIR.Providers;
using Xunit;

namespace WinDFIR.Tests;

public class EventLogProviderGracefulDegradeTests
{
    private const string LogPowerShellOperational = "Microsoft-Windows-PowerShell/Operational";
    private const string LogWmiActivityOperational = "Microsoft-Windows-WMI-Activity/Operational";
    private const string LogTaskSchedulerOperational = "Microsoft-Windows-TaskScheduler/Operational";
    private const string LogDefenderOperational = "Microsoft-Windows-Windows Defender/Operational";
    private const string LogSysmonOperationalPrimary = "Microsoft-Windows-Sysmon/Operational";
    private const string LogSysmonOperationalLegacy = "Sysmon/Operational";

    [Fact]
    public async Task StartStop_WhenReaderFactoryReturnsNull_NoFaultAndCompletes()
    {
        var provider = new EventLogProvider { CreateEventLogReaderForTest = _ => null };
        await provider.StartAsync();
        await Task.Delay(400);
        await provider.StopAsync();
    }

    [Fact]
    public async Task StartStop_WhenReaderFactoryThrowsNotFound_NoFaultAndCompletes()
    {
        var provider = new EventLogProvider
        {
            CreateEventLogReaderForTest = _ => throw new EventLogNotFoundException()
        };
        await provider.StartAsync();
        await Task.Delay(400);
        await provider.StopAsync();
    }

    [Fact]
    public async Task StartStop_WhenReaderFactoryThrowsUnauthorized_NoFaultAndCompletes()
    {
        var provider = new EventLogProvider
        {
            CreateEventLogReaderForTest = _ => throw new UnauthorizedAccessException()
        };
        await provider.StartAsync();
        await Task.Delay(400);
        await provider.StopAsync();
    }

    [Fact]
    public async Task StartStop_WhenReaderFactoryThrowsReadingException_NoFaultAndCompletes()
    {
        var provider = new EventLogProvider
        {
            CreateEventLogReaderForTest = _ => throw new EventLogReadingException()
        };
        await provider.StartAsync();
        await Task.Delay(400);
        await provider.StopAsync();
    }

    [Fact]
    public async Task WhenSecurityUnavailable_StillAttemptsOtherClassicLogs()
    {
        var seen = new ConcurrentBag<string>();
        var provider = new EventLogProvider
        {
            CreateEventLogReaderForTest = name =>
            {
                seen.Add(name);
                // Security often needs elevation; System can be huge to read. Skip both so the test
                // reliably reaches Application without waiting for a full System scan.
                if (name is "Security" or "System")
                    return null;
                return new EventLogReader(name, PathType.LogName);
            }
        };

        try
        {
            await provider.StartAsync();
            await Task.Delay(800);
            await provider.StopAsync();
        }
        finally
        {
            provider.CreateEventLogReaderForTest = null;
        }

        Assert.Contains("Security", seen);
        Assert.Contains("System", seen);
        Assert.Contains("Application", seen);
    }

    [Fact]
    public async Task WhenPrimarySysmonNameFails_NotFound_FallbackNameIsAttempted()
    {
        var order = new ConcurrentQueue<string>();
        var provider = new EventLogProvider
        {
            CreateEventLogReaderForTest = name =>
            {
                order.Enqueue(name);
                // Fast-skip classic and other IR channels so we exercise Sysmon fallback without long reads.
                if (name is "Security" or "System" or "Application")
                    return null;
                if (name is LogPowerShellOperational or LogWmiActivityOperational or LogTaskSchedulerOperational
                    or LogDefenderOperational)
                    return null;
                if (name == LogSysmonOperationalPrimary)
                    throw new EventLogNotFoundException();
                return new EventLogReader(name, PathType.LogName);
            }
        };

        try
        {
            await provider.StartAsync();
            await Task.Delay(800);
            await provider.StopAsync();
        }
        finally
        {
            provider.CreateEventLogReaderForTest = null;
        }

        var names = order.ToArray();
        var i = Array.IndexOf(names, LogSysmonOperationalPrimary);
        var j = Array.IndexOf(names, LogSysmonOperationalLegacy);
        Assert.True(i >= 0, "Expected primary Sysmon channel to be attempted.");
        Assert.True(j > i, "Expected legacy Sysmon channel attempt after primary failure.");
    }
}
