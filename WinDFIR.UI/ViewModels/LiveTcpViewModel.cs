using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Threading.Tasks;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;

namespace WinDFIR.UI.ViewModels;

public class LiveTcpViewModel : BaseViewModel
{
    public static readonly string[] AllStateValues =
    {
        "CLOSED",
        "LISTEN",
        "SYN_SENT",
        "SYN_RECEIVED",
        "ESTABLISHED",
        "FIN_WAIT_1",
        "FIN_WAIT_2",
        "CLOSE_WAIT",
        "CLOSING",
        "ACK",
        "TIME_WAIT",
        "DELETE_TCB"
    };

    private readonly IActivityIndex _index;
    private string _filterText = string.Empty;
    private bool _resolveAddress;
    private bool _showTcpV4 = true;
    private bool _showTcpV6 = true;
    private bool _showUdpV4 = true;
    private bool _showUdpV6 = true;
    private readonly HashSet<string> _stateFilters = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> DnsCache = new(StringComparer.OrdinalIgnoreCase);

    public LiveTcpViewModel(IActivityIndex index)
    {
        _index = index;
    }

    public ObservableCollection<LiveTcpConnectionItem> Connections { get; } = new();

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
                Refresh();
        }
    }

    public bool ResolveAddress
    {
        get => _resolveAddress;
        set => SetProperty(ref _resolveAddress, value);
    }

    public bool ShowTcpV4
    {
        get => _showTcpV4;
        set
        {
            if (SetProperty(ref _showTcpV4, value))
                Refresh();
        }
    }

    public bool ShowTcpV6
    {
        get => _showTcpV6;
        set
        {
            if (SetProperty(ref _showTcpV6, value))
                Refresh();
        }
    }

    public bool ShowUdpV4
    {
        get => _showUdpV4;
        set
        {
            if (SetProperty(ref _showUdpV4, value))
                Refresh();
        }
    }

    public bool ShowUdpV6
    {
        get => _showUdpV6;
        set
        {
            if (SetProperty(ref _showUdpV6, value))
                Refresh();
        }
    }

    public void Refresh()
    {
        var networkEvents = _index.GetEventsByCategory("Network").ToList();
        var processEvents = _index.GetEventsByCategory("Process").ToList();

        var pidMap = BuildProcessMap(processEvents);
        var connectionItems = new List<LiveTcpConnectionItem>();

        var grouped = networkEvents
            .Where(e => e.ObjectNetworkFlow.HasValue)
            .GroupBy(e => e.ObjectNetworkFlow!.Value);

        foreach (var group in grouped)
        {
            var flow = group.Key;
            var lastEvent = group.OrderByDescending(e => e.Timestamp).FirstOrDefault();
            var lastAction = lastEvent?.Action ?? string.Empty;
            var lastState = lastEvent?.Fields.GetValueOrDefault("State")?.ToString() ?? string.Empty;
            var protocol = flow.Protocol;

            var processName = string.Empty;
            var imagePath = string.Empty;
            if (flow.ProcessId.HasValue && pidMap.TryGetValue(flow.ProcessId.Value, out var info))
            {
                processName = info.ProcessName;
                imagePath = info.ImagePath;
            }

            var firstEvent = group.OrderBy(e => e.Timestamp).FirstOrDefault();
            var moduleName = !string.IsNullOrWhiteSpace(imagePath) ? Path.GetFileName(imagePath) : processName;

            var (sentBytes, recvBytes, sentPackets, recvPackets) = NetworkStatsAggregator.GetStats(flow);
            var item = new LiveTcpConnectionItem
            {
                Protocol = protocol,
                LocalEndpoint = flow.LocalEndpoint,
                RemoteEndpoint = flow.RemoteEndpoint,
                ProcessId = flow.ProcessId,
                ProcessName = processName,
                ImagePath = imagePath,
                State = NormalizeState(lastState, lastAction),
                LocalAddress = TryExtractAddress(flow.LocalEndpoint),
                LocalPort = ExtractPort(flow.LocalEndpoint),
                RemoteAddress = TryExtractAddress(flow.RemoteEndpoint),
                RemotePort = ExtractPort(flow.RemoteEndpoint),
                CreateTime = firstEvent?.Timestamp ?? DateTime.MinValue,
                ModuleName = moduleName ?? string.Empty,
                SentPackets = sentPackets,
                RecvPackets = recvPackets,
                SentBytes = sentBytes,
                RecvBytes = recvBytes,
                LastSeen = lastEvent?.Timestamp ?? DateTime.MinValue,
                EventCount = group.Count()
            };
            item.LocalAddressDisplay = item.LocalAddress;
            item.RemoteAddressDisplay = item.RemoteAddress;

            connectionItems.Add(item);
        }

        connectionItems = ApplyProtocolFilters(connectionItems);
        connectionItems = ApplyStateFilters(connectionItems);

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var filterLower = FilterText.ToLowerInvariant();
            connectionItems = connectionItems.Where(item =>
                item.Protocol.ToLowerInvariant().Contains(filterLower) ||
                item.LocalAddress.ToLowerInvariant().Contains(filterLower) ||
                item.RemoteAddress.ToLowerInvariant().Contains(filterLower) ||
                item.LocalAddressDisplay.ToLowerInvariant().Contains(filterLower) ||
                item.RemoteAddressDisplay.ToLowerInvariant().Contains(filterLower) ||
                (item.LocalPort ?? string.Empty).Contains(filterLower) ||
                (item.RemotePort ?? string.Empty).Contains(filterLower) ||
                (item.ProcessName ?? string.Empty).ToLowerInvariant().Contains(filterLower) ||
                (item.ImagePath ?? string.Empty).ToLowerInvariant().Contains(filterLower) ||
                (item.ModuleName ?? string.Empty).ToLowerInvariant().Contains(filterLower) ||
                (item.ProcessId?.ToString() ?? string.Empty).Contains(filterLower) ||
                (item.State ?? string.Empty).ToLowerInvariant().Contains(filterLower)).ToList();
        }

        Connections.Clear();
        foreach (var item in connectionItems.OrderByDescending(i => i.LastSeen))
        {
            Connections.Add(item);
        }

        if (ResolveAddress)
            _ = ResolveAddressesAsync();
    }

    public void Clear()
    {
        Connections.Clear();
    }

    public void SetStateFilter(string state, bool isEnabled)
    {
        if (string.IsNullOrWhiteSpace(state))
            return;

        if (isEnabled)
            _stateFilters.Add(state);
        else
            _stateFilters.Remove(state);

        Refresh();
    }

    public void SetStateFilters(IEnumerable<string> states)
    {
        _stateFilters.Clear();
        foreach (var state in states)
        {
            if (!string.IsNullOrWhiteSpace(state))
                _stateFilters.Add(state);
        }

        Refresh();
    }

    public IReadOnlyCollection<string> GetStateFilters()
    {
        return _stateFilters.ToList().AsReadOnly();
    }

    public async Task ResolveAddressesAsync()
    {
        var snapshot = Connections.ToList();
        var endpoints = snapshot.SelectMany(item => new[] { item.LocalEndpoint, item.RemoteEndpoint })
            .Select(TryExtractAddress)
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var address in endpoints)
        {
            var isIp = IPAddress.TryParse(address, out _);
            var cacheKey = BuildCacheKey(address, isIp);
            if (DnsCache.ContainsKey(cacheKey))
                continue;

            if (isIp)
            {
                try
                {
                    var entry = await Dns.GetHostEntryAsync(address);
                    var host = entry.HostName ?? address;
                    DnsCache[cacheKey] = host;
                }
                catch
                {
                    DnsCache[cacheKey] = address;
                }
            }
            else
            {
                try
                {
                    var entry = await Dns.GetHostEntryAsync(address);
                    var ip = PickBestIp(entry);
                    DnsCache[cacheKey] = string.IsNullOrWhiteSpace(ip) ? address : ip;
                }
                catch
                {
                    DnsCache[cacheKey] = address;
                }
            }
        }

        foreach (var item in snapshot)
        {
            item.LocalAddressDisplay = ResolveAddressOnly(item.LocalAddress);
            item.RemoteAddressDisplay = ResolveAddressOnly(item.RemoteAddress);
        }
    }

    public void ResetResolvedAddresses()
    {
        foreach (var item in Connections)
        {
            item.LocalAddressDisplay = item.LocalAddress;
            item.RemoteAddressDisplay = item.RemoteAddress;
        }
    }

    private static Dictionary<uint, ProcessInfo> BuildProcessMap(List<ActivityEvent> processEvents)
    {
        var map = new Dictionary<uint, ProcessInfo>();
        foreach (var evt in processEvents.OrderByDescending(e => e.Timestamp))
        {
            if (!evt.SubjectProcess.HasValue)
                continue;

            var pid = evt.SubjectProcess.Value.ProcessId;
            if (map.ContainsKey(pid))
                continue;

            var processName = evt.Fields.GetValueOrDefault("ProcessName")?.ToString() ?? string.Empty;
            var imagePath = evt.Fields.GetValueOrDefault("ImagePath")?.ToString() ?? string.Empty;
            map[pid] = new ProcessInfo(processName, imagePath);
        }

        return map;
    }

    private List<LiveTcpConnectionItem> ApplyProtocolFilters(List<LiveTcpConnectionItem> items)
    {
        return items.Where(item =>
        {
            var isTcp = item.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase);
            var isUdp = item.Protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase);
            var family = GetAddressFamily(item.LocalEndpoint);

            if (isTcp && family == AddressFamily.InterNetwork)
                return ShowTcpV4;
            if (isTcp && family == AddressFamily.InterNetworkV6)
                return ShowTcpV6;
            if (isUdp && family == AddressFamily.InterNetwork)
                return ShowUdpV4;
            if (isUdp && family == AddressFamily.InterNetworkV6)
                return ShowUdpV6;

            return true;
        }).ToList();
    }

    private List<LiveTcpConnectionItem> ApplyStateFilters(List<LiveTcpConnectionItem> items)
    {
        if (_stateFilters.Count == 0)
            return items;

        return items.Where(item => _stateFilters.Contains(item.State)).ToList();
    }

    private static AddressFamily GetAddressFamily(string endpoint)
    {
        var address = TryExtractAddress(endpoint);
        if (string.IsNullOrWhiteSpace(address))
            return AddressFamily.Unspecified;

        if (IPAddress.TryParse(address, out var ip))
            return ip.AddressFamily;

        return AddressFamily.Unspecified;
    }

    private static string TryExtractAddress(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return string.Empty;

        if (endpoint.StartsWith("[", StringComparison.Ordinal))
        {
            var end = endpoint.IndexOf("]", StringComparison.Ordinal);
            if (end > 1)
                return endpoint.Substring(1, end - 1);
        }

        var lastColon = endpoint.LastIndexOf(':');
        if (lastColon > 0 && lastColon < endpoint.Length - 1)
            return endpoint.Substring(0, lastColon);

        return endpoint;
    }

    private static string ResolveAddressOnly(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return address;

        var isIp = IPAddress.TryParse(address, out _);
        var cacheKey = BuildCacheKey(address, isIp);
        if (!DnsCache.TryGetValue(cacheKey, out var mapped))
            return address;

        return mapped;
    }

    private static string NormalizeState(string rawState, string action)
    {
        var state = string.IsNullOrWhiteSpace(rawState) ? action : rawState;
        if (string.IsNullOrWhiteSpace(state))
            return "UNKNOWN";
        return state.ToUpperInvariant();
    }

    private static string BuildCacheKey(string address, bool isIp)
    {
        return (isIp ? "ip:" : "host:") + address.ToLowerInvariant();
    }

    private static string ExtractPort(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return string.Empty;

        if (endpoint.StartsWith("[", StringComparison.Ordinal))
        {
            var end = endpoint.IndexOf("]", StringComparison.Ordinal);
            if (end > 0 && end + 1 < endpoint.Length && endpoint[end + 1] == ':')
                return endpoint.Substring(end + 2);
            return string.Empty;
        }

        var lastColon = endpoint.LastIndexOf(':');
        if (lastColon > 0 && lastColon < endpoint.Length - 1)
            return endpoint.Substring(lastColon + 1);

        return string.Empty;
    }

    private static string PickBestIp(IPHostEntry entry)
    {
        if (entry.AddressList == null || entry.AddressList.Length == 0)
            return string.Empty;

        var ipv4 = entry.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
        if (ipv4 != null)
            return ipv4.ToString();

        return entry.AddressList[0].ToString();
    }

    private readonly record struct ProcessInfo(string ProcessName, string ImagePath);
}

public class LiveTcpConnectionItem : BaseViewModel
{
    public string Protocol { get; set; } = string.Empty;
    public string LocalEndpoint { get; set; } = string.Empty;
    public string RemoteEndpoint { get; set; } = string.Empty;
    public string LocalAddress { get; set; } = string.Empty;
    public string? LocalPort { get; set; }
    public string RemoteAddress { get; set; } = string.Empty;
    public string? RemotePort { get; set; }
    public uint? ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public int SentPackets { get; set; }
    public int RecvPackets { get; set; }
    public long SentBytes { get; set; }
    public long RecvBytes { get; set; }
    public DateTime CreateTime { get; set; }
    public int EventCount { get; set; }
    public DateTime LastSeen { get; set; }

    private string _localAddressDisplay = string.Empty;
    public string LocalAddressDisplay
    {
        get => _localAddressDisplay;
        set => SetProperty(ref _localAddressDisplay, value);
    }

    private string _remoteAddressDisplay = string.Empty;
    public string RemoteAddressDisplay
    {
        get => _remoteAddressDisplay;
        set => SetProperty(ref _remoteAddressDisplay, value);
    }
}
