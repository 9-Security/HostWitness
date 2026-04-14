namespace WinDFIR.Providers;

public sealed class ProcessCreateCacheStats
{
    public string ProviderName { get; init; } = "Unknown";
    public int TotalEntries { get; init; }
    public int ProvisionalEntries { get; init; }
    public int LongLivedEntries { get; init; }
    public int MaxEntries { get; init; }
    public TimeSpan ProvisionalTtl { get; init; }
    public TimeSpan AuthoritativeTtl { get; init; }
    public TimeSpan LongLivedTtl { get; init; }
}

public interface IProcessCreateCacheStatsProvider
{
    ProcessCreateCacheStats GetProcessCreateCacheStats();
}
