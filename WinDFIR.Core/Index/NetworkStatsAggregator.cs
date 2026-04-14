using System.Collections.Concurrent;
using WinDFIR.Core.Entities;

namespace WinDFIR.Core.Index;

/// <summary>
/// Aggregates per-connection sent/recv bytes (and optionally packets) for Live TCP View.
/// Fed by ETW or other providers when TransferSize/size is available; consumed by LiveTcpViewModel.
/// </summary>
public static class NetworkStatsAggregator
{
    private static readonly ConcurrentDictionary<string, (long SentBytes, long RecvBytes, int SentPackets, int RecvPackets)> _stats = new();

    private static string Key(NetworkFlowKey flow)
    {
        return $"{flow.Protocol}|{flow.LocalEndpoint}|{flow.RemoteEndpoint}|{flow.ProcessId ?? 0}";
    }

    public static void AddSent(NetworkFlowKey flow, long bytes = 0, int packets = 1)
    {
        var k = Key(flow);
        _stats.AddOrUpdate(k, (bytes, 0, packets, 0), (_, t) => (t.SentBytes + bytes, t.RecvBytes, t.SentPackets + packets, t.RecvPackets));
    }

    public static void AddRecv(NetworkFlowKey flow, long bytes = 0, int packets = 1)
    {
        var k = Key(flow);
        _stats.AddOrUpdate(k, (0, bytes, 0, packets), (_, t) => (t.SentBytes, t.RecvBytes + bytes, t.SentPackets, t.RecvPackets + packets));
    }

    public static (long SentBytes, long RecvBytes, int SentPackets, int RecvPackets) GetStats(NetworkFlowKey flow)
    {
        return _stats.TryGetValue(Key(flow), out var t) ? t : (0, 0, 0, 0);
    }

    public static void Clear()
    {
        _stats.Clear();
    }
}
