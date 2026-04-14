using WinDFIR.Providers;
using Xunit;

namespace WinDFIR.Tests;

public class EventLogProviderMappingTests
{
    [Theory]
    [InlineData("Security", 4624, "Logon", "Start")]
    [InlineData("Security", 4688, "Process", "Start")]
    [InlineData("Security", 4697, "Service", "Query")]
    [InlineData("Security", 4698, "ScheduledTask", "Query")]
    [InlineData("Security", 4699, "ScheduledTask", "Query")]
    [InlineData("Security", 4700, "ScheduledTask", "Query")]
    [InlineData("Security", 4701, "ScheduledTask", "Query")]
    [InlineData("System", 6009, "System", "Start")]
    [InlineData("Application", 1000, "Application", "Query")]
    public void MapEventToCategoryAction_ClassicLogs_Unchanged(string log, int id, string category, string action)
    {
        var (c, a) = EventLogProvider.MapEventToCategoryActionForTest(id, log);
        Assert.Equal(category, c);
        Assert.Equal(action, a);
    }

    [Theory]
    [InlineData("Microsoft-Windows-PowerShell/Operational", 4103, "PowerShell", "Module")]
    [InlineData("Microsoft-Windows-PowerShell/Operational", 4104, "PowerShell", "ScriptBlock")]
    [InlineData("Microsoft-Windows-PowerShell/Operational", 99999, "PowerShell", "Query")]
    [InlineData("Microsoft-Windows-WMI-Activity/Operational", 5857, "WMI", "Query")]
    [InlineData("Microsoft-Windows-TaskScheduler/Operational", 106, "ScheduledTask", "Register")]
    [InlineData("Microsoft-Windows-TaskScheduler/Operational", 200, "ScheduledTask", "Start")]
    [InlineData("Microsoft-Windows-TaskScheduler/Operational", 201, "ScheduledTask", "Stop")]
    [InlineData("Microsoft-Windows-Windows Defender/Operational", 1116, "Antimalware", "Detection")]
    [InlineData("Microsoft-Windows-Windows Defender/Operational", 1117, "Antimalware", "Action")]
    [InlineData("Microsoft-Windows-Sysmon/Operational", 1, "Process", "Start")]
    [InlineData("Microsoft-Windows-Sysmon/Operational", 5, "Process", "Stop")]
    [InlineData("Microsoft-Windows-Sysmon/Operational", 22, "DNS", "Query")]
    [InlineData("Sysmon/Operational", 1, "Process", "Start")]
    public void MapEventToCategoryAction_IrChannels_ReasonableMapping(string log, int id, string category, string action)
    {
        var (c, a) = EventLogProvider.MapEventToCategoryActionForTest(id, log);
        Assert.Equal(category, c);
        Assert.Equal(action, a);
    }
}
