namespace WinDFIR.Providers;

public sealed class EtwThrottleStats
{
    public Dictionary<string, int> LastReportedDrops { get; init; } = new();
    public Dictionary<string, int> TotalDrops { get; init; } = new();
    public DateTime LastReportUtc { get; init; }
}

public interface IEtwThrottleStatsProvider
{
    EtwThrottleStats GetEtwThrottleStats();
}
