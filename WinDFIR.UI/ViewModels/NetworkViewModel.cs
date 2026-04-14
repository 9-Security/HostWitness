using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Snapshot;

namespace WinDFIR.UI.ViewModels;

/// <summary>
/// View model for Network view with connection monitoring and drill-down.
/// </summary>
public class NetworkViewModel : BaseViewModel
{
    private readonly IActivityIndex _index;
    private NetworkFlowKey? _selectedConnection;
    private ObservableCollection<NetworkConnectionItem> _connections = new();

    public NetworkViewModel(IActivityIndex index)
    {
        _index = index;
    }

    public ObservableCollection<NetworkConnectionItem> Connections
    {
        get => _connections;
        set => SetProperty(ref _connections, value);
    }

    public NetworkFlowKey? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (SetProperty(ref _selectedConnection, value))
            {
                OnSelectedConnectionChanged();
            }
        }
    }

    public ObservableCollection<ActivityEvent> RelatedEvents { get; } = new();
    public HashSet<NetworkFlowKey> HighlightedConnections { get; } = new();

    public void Refresh()
    {
        var previousSelection = SelectedConnection;
        var networkEvents = _index.GetEventsByCategory("Network").ToList();
        var connectionKeys = new HashSet<NetworkFlowKey>();

        foreach (var evt in networkEvents)
        {
            if (evt.ObjectNetworkFlow.HasValue)
            {
                connectionKeys.Add(evt.ObjectNetworkFlow.Value);
            }
        }

        // Clear existing items and add new ones to the same collection
        // This ensures the binding is maintained
        _connections.Clear();
        
        foreach (var flowKey in connectionKeys)
        {
            var events = _index.GetEventsByNetworkFlow(flowKey).ToList();
            var connectEvent = events.FirstOrDefault(e => e.Action == "Connect");

            var item = new NetworkConnectionItem
            {
                NetworkFlowKey = flowKey,
                Protocol = flowKey.Protocol,
                LocalEndpoint = flowKey.LocalEndpoint,
                RemoteEndpoint = flowKey.RemoteEndpoint,
                ProcessId = flowKey.ProcessId ?? ResolveProcessId(events),
                State = connectEvent?.Fields.GetValueOrDefault("State")?.ToString() ?? "Unknown",
                EventCount = events.Count(),
                FirstSeen = events.Min(e => e.Timestamp),
                LastSeen = events.Max(e => e.Timestamp)
            };

            _connections.Add(item);
        }

        // Notify that the collection has changed
        OnPropertyChanged(nameof(Connections));

        if (previousSelection.HasValue && connectionKeys.Contains(previousSelection.Value))
        {
            if (SelectedConnection != previousSelection)
                SelectedConnection = previousSelection;
            else
                OnSelectedConnectionChanged();
        }
        else
        {
            SelectedConnection = null;
        }
    }

    public void ApplyDiff(SnapshotDiff diff)
    {
        HighlightedConnections.Clear();
        foreach (var connectionKey in diff.NewConnections)
        {
            HighlightedConnections.Add(connectionKey);
        }
        Refresh();
    }

    private void OnSelectedConnectionChanged()
    {
        RelatedEvents.Clear();

        if (SelectedConnection.HasValue)
        {
            var events = _index.GetEventsByNetworkFlow(SelectedConnection.Value);
            foreach (var evt in events)
            {
                RelatedEvents.Add(evt);
            }
        }
    }

    private static uint? ResolveProcessId(IEnumerable<ActivityEvent> events)
    {
        foreach (var evt in events.OrderByDescending(e => e.Timestamp))
        {
            if (evt.Fields.TryGetValue("ProcessId", out var value) && value != null)
            {
                if (value is uint uintValue)
                    return uintValue;

                if (uint.TryParse(value.ToString(), out var parsed))
                    return parsed;
            }
        }

        return null;
    }
}

/// <summary>
/// Item for network connection view.
/// </summary>
public class NetworkConnectionItem
{
    public NetworkFlowKey NetworkFlowKey { get; set; }
    public string Protocol { get; set; } = string.Empty;
    public string LocalEndpoint { get; set; } = string.Empty;
    public string RemoteEndpoint { get; set; } = string.Empty;
    public uint? ProcessId { get; set; }
    public string State { get; set; } = string.Empty;
    public int EventCount { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}
