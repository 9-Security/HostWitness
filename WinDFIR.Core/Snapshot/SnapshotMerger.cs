using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;

namespace WinDFIR.Core.Snapshot;

/// <summary>
/// Merges live state into snapshot.
/// Per specification: Promote live state to snapshot.
/// </summary>
public class SnapshotMerger
{
    /// <summary>
    /// Merges live index state into a snapshot-ready format.
    /// </summary>
    public static void MergeLiveToSnapshot(IActivityIndex liveIndex, IActivityIndex snapshotIndex)
    {
        // Get all events from live index
        var liveEvents = liveIndex.GetEventsByTimeRange(DateTime.MinValue, DateTime.MaxValue);

        // Add all live events to snapshot index
        foreach (var evt in liveEvents)
        {
            snapshotIndex.AddEvent(evt);
        }
    }

    /// <summary>
    /// Creates a diff between two indexes, highlighting new processes and connections.
    /// </summary>
    public static SnapshotDiff CreateDiff(IActivityIndex baselineIndex, IActivityIndex currentIndex)
    {
        var diff = new SnapshotDiff();
        var newProcessSet = new HashSet<ProcessKey>();
        var newConnectionSet = new HashSet<NetworkFlowKey>();

        // Get all events from both indexes
        var baselineEvents = baselineIndex.GetEventsByTimeRange(DateTime.MinValue, DateTime.MaxValue).ToList();
        var currentEvents = currentIndex.GetEventsByTimeRange(DateTime.MinValue, DateTime.MaxValue).ToList();

        // Find new processes
        var baselineProcesses = new HashSet<string>();
        var currentProcesses = new HashSet<string>();

        foreach (var evt in baselineEvents)
        {
            if (evt.SubjectProcess.HasValue)
            {
                baselineProcesses.Add(evt.SubjectProcess.Value.ToString());
            }
        }

        foreach (var evt in currentEvents)
        {
            if (evt.SubjectProcess.HasValue)
            {
                var processKey = evt.SubjectProcess.Value.ToString();
                currentProcesses.Add(processKey);
                
                if (!baselineProcesses.Contains(processKey) && newProcessSet.Add(evt.SubjectProcess.Value))
                {
                    diff.NewProcesses.Add(evt.SubjectProcess.Value);
                }
            }
        }

        // Find new network connections
        var baselineConnections = new HashSet<string>();
        var currentConnections = new HashSet<string>();

        foreach (var evt in baselineEvents)
        {
            if (evt.ObjectNetworkFlow.HasValue)
            {
                baselineConnections.Add(evt.ObjectNetworkFlow.Value.ToString());
            }
        }

        foreach (var evt in currentEvents)
        {
            if (evt.ObjectNetworkFlow.HasValue)
            {
                var connectionKey = evt.ObjectNetworkFlow.Value.ToString();
                currentConnections.Add(connectionKey);
                
                if (!baselineConnections.Contains(connectionKey) && newConnectionSet.Add(evt.ObjectNetworkFlow.Value))
                {
                    diff.NewConnections.Add(evt.ObjectNetworkFlow.Value);
                }
            }
        }

        return diff;
    }
}

/// <summary>
/// Represents differences between two snapshots.
/// </summary>
public class SnapshotDiff
{
    public List<ProcessKey> NewProcesses { get; } = new();
    public List<NetworkFlowKey> NewConnections { get; } = new();
}
