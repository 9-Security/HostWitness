using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Normalization;

namespace WinDFIR.Providers;

/// <summary>
/// Network connection provider: monitors TCP/UDP connections and maps them to processes.
/// Per specification: TCP/UDP connections mapped to processes.
/// </summary>
public class NetConnectionProvider : IProvider
{
    public string Name => "NetConnectionProvider";

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _monitoringTask;
    private readonly Dictionary<string, (NetworkFlowKey FlowKey, TcpState LastState)> _connectionCache = new();
    private readonly object _cacheLock = new();

    public event EventHandler<ActivityEvent>? EventProduced;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_monitoringTask != null && !_monitoringTask.IsCompleted)
            return Task.CompletedTask;

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitoringTask = Task.Run(() => MonitorConnections(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cancellationTokenSource?.Cancel();

        if (_monitoringTask != null)
        {
            try
            {
                await _monitoringTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
            }
        }
    }

    private async Task MonitorConnections(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnumerateConnections(cancellationToken);
            }
            catch (Exception)
            {
                // Continue monitoring despite errors
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private Task EnumerateConnections(CancellationToken cancellationToken)
    {
        var tcpPidMap = GetTcpPidMap();
        var udpPidMap = GetUdpPidMap();

        var tcpConnections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
        var udpListeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners();

        var currentConnections = new HashSet<string>();

        // Process TCP connections
        foreach (var conn in tcpConnections)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.CompletedTask;

            try
            {
                var localEndpoint = FormatEndpoint(conn.LocalEndPoint.Address, conn.LocalEndPoint.Port);
                var remoteEndpoint = FormatEndpoint(conn.RemoteEndPoint.Address, conn.RemoteEndPoint.Port);
                var pidKey = $"{localEndpoint}-{remoteEndpoint}";
                uint? processId = tcpPidMap.TryGetValue(pidKey, out var tcpPid) ? tcpPid : (uint?)null;
                var timeBucket = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour); // Hourly bucket

                var pidToken = processId.HasValue ? processId.Value.ToString() : "NA";
                var connectionKey = $"{localEndpoint}-{remoteEndpoint}-TCP-{pidToken}";
                currentConnections.Add(connectionKey);

                var networkFlowKey = KeyGenerator.GenerateNetworkFlowKey(
                    "TCP",
                    localEndpoint,
                    remoteEndpoint,
                    processId,
                    timeBucket);

                lock (_cacheLock)
                {
                    UpdateConnectionObservation(connectionKey, networkFlowKey, "Connect", conn.State, processId);
                }
            }
            catch
            {
                // Skip connections we can't process
                continue;
            }
        }

        // Process UDP listeners
        foreach (var listener in udpListeners)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.CompletedTask;

            try
            {
                var localEndpoint = FormatEndpoint(listener.Address, listener.Port);
                uint? processId = udpPidMap.TryGetValue(localEndpoint, out var udpPid) ? udpPid : (uint?)null;
                var remoteEndpoint = "*:*"; // UDP doesn't have remote endpoint
                var timeBucket = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);

                var pidToken = processId.HasValue ? processId.Value.ToString() : "NA";
                var connectionKey = $"{localEndpoint}-UDP-{pidToken}";
                currentConnections.Add(connectionKey);

                var networkFlowKey = KeyGenerator.GenerateNetworkFlowKey(
                    "UDP",
                    localEndpoint,
                    remoteEndpoint,
                    processId,
                    timeBucket);

                lock (_cacheLock)
                {
                    UpdateConnectionObservation(connectionKey, networkFlowKey, "Listen", TcpState.Unknown, processId);
                }
            }
            catch
            {
                // Skip listeners we can't process
                continue;
            }
        }

        // Check for closed connections
        lock (_cacheLock)
        {
            var closedConnections = _connectionCache.Keys.Except(currentConnections).ToList();
            foreach (var connKey in closedConnections)
            {
                var networkFlowKey = _connectionCache[connKey].FlowKey;
                ProduceConnectionEvent(networkFlowKey, "Disconnect", TcpState.Unknown, null);
                _connectionCache.Remove(connKey);
            }
        }

        return Task.CompletedTask;
    }

    private void UpdateConnectionObservation(string connectionKey, NetworkFlowKey networkFlowKey, string openAction, TcpState state, uint? processId)
    {
        if (!_connectionCache.TryGetValue(connectionKey, out var cached))
        {
            _connectionCache[connectionKey] = (networkFlowKey, state);
            ProduceConnectionEvent(networkFlowKey, openAction, state, processId);
            return;
        }

        var cachedKey = cached.FlowKey;
        var lastState = cached.LastState;

        if (cachedKey.TimeBucket != networkFlowKey.TimeBucket)
        {
            ProduceConnectionEvent(cachedKey, "Disconnect", TcpState.Unknown, cachedKey.ProcessId);
            _connectionCache[connectionKey] = (networkFlowKey, state);
            ProduceConnectionEvent(networkFlowKey, openAction, state, processId);
            return;
        }

        // Same time bucket: emit when TCP state or resolved PID changes so Live TCP View stays current.
        var pidChanged = cachedKey.ProcessId != networkFlowKey.ProcessId;
        var stateChanged = string.Equals(networkFlowKey.Protocol, "TCP", StringComparison.OrdinalIgnoreCase) &&
                           state != lastState;

        if (stateChanged || pidChanged)
        {
            ProduceConnectionEvent(networkFlowKey, "Update", state, processId);
        }

        _connectionCache[connectionKey] = (networkFlowKey, state);
    }

    private void ProduceConnectionEvent(NetworkFlowKey networkFlowKey, string action, TcpState state, uint? processId)
    {
        var evidence = new List<EvidenceRef>
        {
            new EvidenceRef("NetConnectionProvider", 
                $"{networkFlowKey.Protocol}:{networkFlowKey.LocalEndpoint}->{networkFlowKey.RemoteEndpoint}",
                null,
                DateTime.UtcNow)
        };

        var activityEvent = new ActivityEvent
        {
            Category = "Network",
            Action = action,
            Timestamp = DateTime.UtcNow,
            Evidence = evidence,
            ObjectNetworkFlow = networkFlowKey,
            Summary = $"Network {action.ToLower()}: {networkFlowKey.Protocol} {networkFlowKey.LocalEndpoint} -> {networkFlowKey.RemoteEndpoint}",
            Fields = new Dictionary<string, object>
            {
                ["Protocol"] = networkFlowKey.Protocol,
                ["LocalEndpoint"] = networkFlowKey.LocalEndpoint,
                ["RemoteEndpoint"] = networkFlowKey.RemoteEndpoint,
                ["ProcessId"] = processId ?? 0,
                ["State"] = state.ToString()
            },
            Confidence = "High"
        };

        EventProduced?.Invoke(this, activityEvent);
    }

    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    private delegate uint ExtendedTableQuery(IntPtr buffer, ref int bufferSize);

    /// <summary>
    /// Probes the required size, allocates, and queries an Extended*Table API, retrying when the table
    /// grew between the size probe and the read (the API returns ERROR_INSUFFICIENT_BUFFER with the new
    /// size). Returns IntPtr.Zero on failure; on success the caller must FreeHGlobal the returned buffer.
    /// Connection tables change constantly, so a single-shot probe would routinely lose rows under churn.
    /// </summary>
    private static IntPtr AllocAndQueryTable(ExtendedTableQuery query)
    {
        var bufferSize = 0;
        query(IntPtr.Zero, ref bufferSize);

        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (bufferSize <= 0)
                return IntPtr.Zero;

            var buffer = Marshal.AllocHGlobal(bufferSize);
            var result = query(buffer, ref bufferSize);
            if (result == 0)
                return buffer;

            Marshal.FreeHGlobal(buffer);
            if (result != ERROR_INSUFFICIENT_BUFFER)
                return IntPtr.Zero;
            // bufferSize now holds the larger required size; loop and retry.
        }

        return IntPtr.Zero;
    }

    private static Dictionary<string, uint> GetTcpPidMap()
    {
        var map = new Dictionary<string, uint>();
        AddTcpRows(map, AF_INET);
        AddTcpRows(map, AF_INET6);
        return map;
    }

    private static void AddTcpRows(Dictionary<string, uint> map, int family)
    {
        try
        {
            var buffer = AllocAndQueryTable(
                (IntPtr b, ref int s) => GetExtendedTcpTable(b, ref s, true, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0));
            if (buffer == IntPtr.Zero)
                return;

            try
            {
                var rowCount = Marshal.ReadInt32(buffer);
                var rowPtr = IntPtr.Add(buffer, sizeof(int));

                if (family == AF_INET)
                {
                    var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                    for (var i = 0; i < rowCount; i++)
                    {
                        var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                        var key = $"{FormatEndpoint(new IPAddress(row.localAddr), ConvertPort(row.localPort))}-{FormatEndpoint(new IPAddress(row.remoteAddr), ConvertPort(row.remotePort))}";
                        map[key] = row.owningPid;
                        rowPtr = IntPtr.Add(rowPtr, rowSize);
                    }
                }
                else
                {
                    var rowSize = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
                    for (var i = 0; i < rowCount; i++)
                    {
                        var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(rowPtr);
                        var key = $"{FormatEndpoint(new IPAddress(row.localAddr, row.localScopeId), ConvertPort(row.localPort))}-{FormatEndpoint(new IPAddress(row.remoteAddr, row.remoteScopeId), ConvertPort(row.remotePort))}";
                        map[key] = row.owningPid;
                        rowPtr = IntPtr.Add(rowPtr, rowSize);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            // Ignore failures for this address family
        }
    }

    private static Dictionary<string, uint> GetUdpPidMap()
    {
        var map = new Dictionary<string, uint>();
        AddUdpRows(map, AF_INET);
        AddUdpRows(map, AF_INET6);
        return map;
    }

    private static void AddUdpRows(Dictionary<string, uint> map, int family)
    {
        try
        {
            var buffer = AllocAndQueryTable(
                (IntPtr b, ref int s) => GetExtendedUdpTable(b, ref s, true, family, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0));
            if (buffer == IntPtr.Zero)
                return;

            try
            {
                var rowCount = Marshal.ReadInt32(buffer);
                var rowPtr = IntPtr.Add(buffer, sizeof(int));

                if (family == AF_INET)
                {
                    var rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                    for (var i = 0; i < rowCount; i++)
                    {
                        var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                        var key = FormatEndpoint(new IPAddress(row.localAddr), ConvertPort(row.localPort));
                        map[key] = row.owningPid;
                        rowPtr = IntPtr.Add(rowPtr, rowSize);
                    }
                }
                else
                {
                    var rowSize = Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>();
                    for (var i = 0; i < rowCount; i++)
                    {
                        var row = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(rowPtr);
                        var key = FormatEndpoint(new IPAddress(row.localAddr, row.localScopeId), ConvertPort(row.localPort));
                        map[key] = row.owningPid;
                        rowPtr = IntPtr.Add(rowPtr, rowSize);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            // Ignore failures for this address family
        }
    }

    private static ushort ConvertPort(uint port)
    {
        return (ushort)IPAddress.NetworkToHostOrder((short)port);
    }

    private static string FormatEndpoint(IPAddress address, int port)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // Drop the IPv6 scope id (e.g. "%12"). The live connection table (GetActiveTcpConnections)
            // does not carry it, so retaining it here would make PID-map keys never match those lookups
            // and every IPv6 flow would lose its process attribution.
            var normalized = address.ScopeId != 0 ? new IPAddress(address.GetAddressBytes()) : address;
            return $"[{normalized}]:{port}";
        }
        return $"{address}:{port}";
    }

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;

    private enum TCP_TABLE_CLASS
    {
        TCP_TABLE_BASIC_LISTENER,
        TCP_TABLE_BASIC_CONNECTIONS,
        TCP_TABLE_BASIC_ALL,
        TCP_TABLE_OWNER_PID_LISTENER,
        TCP_TABLE_OWNER_PID_CONNECTIONS,
        TCP_TABLE_OWNER_PID_ALL,
        TCP_TABLE_OWNER_MODULE_LISTENER,
        TCP_TABLE_OWNER_MODULE_CONNECTIONS,
        TCP_TABLE_OWNER_MODULE_ALL
    }

    private enum UDP_TABLE_CLASS
    {
        UDP_TABLE_BASIC,
        UDP_TABLE_OWNER_PID,
        UDP_TABLE_OWNER_MODULE
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr;
        public uint localPort;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] remoteAddr;
        public uint remoteScopeId;
        public uint remotePort;
        public uint state;
        public uint owningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] localAddr;
        public uint localScopeId;
        public uint localPort;
        public uint owningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        TCP_TABLE_CLASS tblClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int dwOutBufLen,
        bool sort,
        int ipVersion,
        UDP_TABLE_CLASS tblClass,
        uint reserved);
}

