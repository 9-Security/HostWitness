namespace WinDFIR.Core.Entities;

/// <summary>
/// Global network flow identifier: (Proto, LocalEP, RemoteEP, PID, TimeBucket)
/// Per specification: NetworkFlow = (Proto, LocalEP, RemoteEP, PID, TimeBucket)
/// </summary>
public readonly record struct NetworkFlowKey
{
    public string Protocol { get; init; }
    public string LocalEndpoint { get; init; }
    public string RemoteEndpoint { get; init; }
    public uint? ProcessId { get; init; }
    public DateTime TimeBucket { get; init; }

    public NetworkFlowKey(string protocol, string localEndpoint, string remoteEndpoint, uint? processId, DateTime timeBucket)
    {
        Protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        LocalEndpoint = localEndpoint ?? throw new ArgumentNullException(nameof(localEndpoint));
        RemoteEndpoint = remoteEndpoint ?? throw new ArgumentNullException(nameof(remoteEndpoint));
        ProcessId = processId;
        TimeBucket = timeBucket;
    }

    public override string ToString() => $"N:{Protocol}:{LocalEndpoint}->{RemoteEndpoint}:{ProcessId}:{TimeBucket:O}";
}

/// <summary>
/// Legacy NetworkKey for backward compatibility during migration.
/// Will be removed after full migration to NetworkFlowKey.
/// </summary>
[Obsolete("Use NetworkFlowKey instead")]
public readonly record struct NetworkKey
{
    public string LocalAddress { get; init; }
    public ushort LocalPort { get; init; }
    public string RemoteAddress { get; init; }
    public ushort RemotePort { get; init; }
    public string Protocol { get; init; }

    public NetworkKey(string localAddress, ushort localPort, string remoteAddress, ushort remotePort, string protocol)
    {
        LocalAddress = localAddress;
        LocalPort = localPort;
        RemoteAddress = remoteAddress;
        RemotePort = remotePort;
        Protocol = protocol;
    }

    public override string ToString() => $"N:{Protocol}:{LocalAddress}:{LocalPort}->{RemoteAddress}:{RemotePort}";
}
