using WinDFIR.Core.Entities;

namespace WinDFIR.Core.Index;

/// <summary>
/// Read-only activity index backed by a list of events (e.g. loaded from a snapshot).
/// AddEvent is a no-op; use only for offline snapshot viewing.
/// </summary>
public sealed class SnapshotActivityIndex : IActivityIndex
{
    private readonly List<ActivityEvent> _events;

    public SnapshotActivityIndex(IEnumerable<ActivityEvent> events)
    {
        _events = events?.ToList() ?? new List<ActivityEvent>();
    }

    public int EventCount => _events.Count;

    public void AddEvent(ActivityEvent activityEvent)
    {
        // No-op: snapshot is read-only
    }

    public IEnumerable<ActivityEvent> GetEventsByProcess(ProcessKey processKey) =>
        _events.Where(e => e.SubjectProcess.HasValue && e.SubjectProcess.Value.Equals(processKey));

    public IEnumerable<ActivityEvent> GetEventsByUser(UserKey userKey) =>
        _events.Where(e => e.SubjectUser.HasValue && e.SubjectUser.Value.Equals(userKey));

    public IEnumerable<ActivityEvent> GetEventsByFile(FileKey fileKey) =>
        _events.Where(e => e.ObjectFile.HasValue && e.ObjectFile.Value.Equals(fileKey));

    public IEnumerable<ActivityEvent> GetEventsByNetworkFlow(NetworkFlowKey networkFlowKey) =>
        _events.Where(e => e.ObjectNetworkFlow.HasValue && e.ObjectNetworkFlow.Value.Equals(networkFlowKey));

    public IEnumerable<ActivityEvent> GetEventsByTimeRange(DateTime start, DateTime end) =>
        _events.Where(e => e.Timestamp >= start && e.Timestamp <= end);

    public IEnumerable<ActivityEvent> GetEventsByCategoryAndAction(string category, string action) =>
        _events.Where(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(e.Action, action, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ActivityEvent> GetEventsByCategory(string category) =>
        _events.Where(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ActivityEvent> GetEventsByField(string fieldName, object? fieldValue) =>
        _events.Where(e => e.Fields.TryGetValue(fieldName, out var v) && Equals(v, fieldValue));

    public void Clear()
    {
        _events.Clear();
    }

#pragma warning disable CS0618
    public IEnumerable<ActivityEvent> GetEventsByNetwork(NetworkKey networkKey) =>
        _events.Where(e => e.ObjectNetworkFlow.HasValue &&
                          string.Equals(e.ObjectNetworkFlow.Value.Protocol, networkKey.Protocol, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ActivityEvent> GetEventsByType(string eventType)
    {
        var parts = eventType.Split('.');
        if (parts.Length == 2)
            return GetEventsByCategoryAndAction(parts[0], parts[1]);
        return GetEventsByCategory(eventType);
    }

    public IEnumerable<ActivityEvent> GetEventsByProperty(string propertyName, object? propertyValue) =>
        GetEventsByField(propertyName, propertyValue);
#pragma warning restore CS0618
}
