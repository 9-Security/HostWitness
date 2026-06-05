using System.Collections.Concurrent;
using System.Threading;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Normalization;

namespace WinDFIR.Core.Index;

/// <summary>
/// In-memory implementation of IActivityIndex.
/// Uses concurrent collections for thread-safe access.
/// Per specification: indexes by ProcessKey, UserKey, FileKey, Time range.
/// Supports configurable max events (0 = unbounded) and periodic TrimAllQueues to reclaim memory from secondary indexes.
/// </summary>
public class InMemoryActivityIndex : IActivityIndex
{
    private const int DefaultMaxEvents = 200_000;
    private const int EvictionBatchSize = 500;

    private readonly int _maxEvents;
    private readonly ConcurrentQueue<IndexedEvent> _allEvents = new();
    private readonly ConcurrentDictionary<ProcessKey, ConcurrentQueue<IndexedEvent>> _eventsByProcess = new();
    private readonly ConcurrentDictionary<UserKey, ConcurrentQueue<IndexedEvent>> _eventsByUser = new();
    private readonly ConcurrentDictionary<FileKey, ConcurrentQueue<IndexedEvent>> _eventsByFile = new();
    private readonly ConcurrentDictionary<NetworkFlowKey, ConcurrentQueue<IndexedEvent>> _eventsByNetworkFlow = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<IndexedEvent>> _eventsByCategory = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<IndexedEvent>> _eventsByCategoryAndAction = new();
    private readonly ConcurrentDictionary<long, byte> _activeEventIds = new();
    private long _eventCount;
    private long _nextEventId;
    private long _evictedEvents;
    private long _droppedEvents;

    public long EvictedEvents => Interlocked.Read(ref _evictedEvents);
    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);
    public long EventCount => Interlocked.Read(ref _eventCount);
    public int MaxEventCapacity => _maxEvents;

    public InMemoryActivityIndex(int? maxEvents = null)
    {
        _maxEvents = maxEvents switch
        {
            null => DefaultMaxEvents,
            <= 0 => 0,
            _ => maxEvents.Value
        };
    }

    public void AddEvent(ActivityEvent activityEvent)
    {
        activityEvent = ActivityEventNormalizer.Normalize(activityEvent);
        var id = Interlocked.Increment(ref _nextEventId);
        var indexed = new IndexedEvent(id, activityEvent);

        // Register the id as active and count it BEFORE publishing to _allEvents. If we enqueued first,
        // a concurrent EnforceCapacity could dequeue this event before TryAdd ran, fail to remove it
        // (id not yet present), and then this thread would mark it active — leaving a phantom-active id
        // whose event is gone from _allEvents (permanent _eventCount drift + secondary-index leak).
        _activeEventIds.TryAdd(id, 0);
        Interlocked.Increment(ref _eventCount);
        _allEvents.Enqueue(indexed);

        // Index by ProcessKey (SubjectProcess)
        if (activityEvent.SubjectProcess.HasValue)
        {
            AddToIndex(_eventsByProcess, activityEvent.SubjectProcess.Value, indexed);
        }

        // Index by UserKey (SubjectUser)
        if (activityEvent.SubjectUser.HasValue)
        {
            AddToIndex(_eventsByUser, activityEvent.SubjectUser.Value, indexed);
        }

        // Index by FileKey (ObjectFile)
        if (activityEvent.ObjectFile.HasValue)
        {
            AddToIndex(_eventsByFile, activityEvent.ObjectFile.Value, indexed);
        }

        // Index by NetworkFlowKey (ObjectNetworkFlow)
        if (activityEvent.ObjectNetworkFlow.HasValue)
        {
            AddToIndex(_eventsByNetworkFlow, activityEvent.ObjectNetworkFlow.Value, indexed);
        }

        // Index by Category
        AddToIndex(_eventsByCategory, activityEvent.Category, indexed);

        // Index by Category+Action
        var normalizedAction = ActivityEventNormalizer.NormalizeAction(activityEvent.Action);
        var categoryAction = $"{activityEvent.Category}.{normalizedAction}";
        AddToIndex(_eventsByCategoryAndAction, categoryAction, indexed);

        EnforceCapacity();
    }

    public IEnumerable<ActivityEvent> GetEventsByProcess(ProcessKey processKey)
    {
        return _eventsByProcess.TryGetValue(processKey, out var events)
            ? FilterActive(events)
            : Enumerable.Empty<ActivityEvent>();
    }

    public IEnumerable<ActivityEvent> GetEventsByUser(UserKey userKey)
    {
        return _eventsByUser.TryGetValue(userKey, out var events)
            ? FilterActive(events)
            : Enumerable.Empty<ActivityEvent>();
    }

    public IEnumerable<ActivityEvent> GetEventsByFile(FileKey fileKey)
    {
        return _eventsByFile.TryGetValue(fileKey, out var events)
            ? FilterActive(events)
            : Enumerable.Empty<ActivityEvent>();
    }

    public IEnumerable<ActivityEvent> GetEventsByNetworkFlow(NetworkFlowKey networkFlowKey)
    {
        return _eventsByNetworkFlow.TryGetValue(networkFlowKey, out var events)
            ? FilterActive(events)
            : Enumerable.Empty<ActivityEvent>();
    }

    public IEnumerable<ActivityEvent> GetEventsByTimeRange(DateTime start, DateTime end)
    {
        return _allEvents
            .Where(e => IsActive(e.Id) && e.Event.Timestamp >= start && e.Event.Timestamp <= end)
            .Select(e => e.Event);
    }

    public IEnumerable<ActivityEvent> GetEventsByCategoryAndAction(string category, string action)
    {
        var normalizedAction = ActivityEventNormalizer.NormalizeAction(action);
        var key = $"{category}.{normalizedAction}";
        return _eventsByCategoryAndAction.TryGetValue(key, out var events)
            ? FilterActive(events)
            : Enumerable.Empty<ActivityEvent>();
    }

    public IEnumerable<ActivityEvent> GetEventsByCategory(string category)
    {
        return _eventsByCategory.TryGetValue(category, out var events)
            ? FilterActive(events)
            : Enumerable.Empty<ActivityEvent>();
    }

    public IEnumerable<ActivityEvent> GetEventsByField(string fieldName, object? fieldValue)
    {
        return _allEvents
            .Where(e => IsActive(e.Id) &&
                        e.Event.Fields.TryGetValue(fieldName, out var value) &&
                        Equals(value, fieldValue))
            .Select(e => e.Event);
    }

    public void Clear()
    {
        _allEvents.Clear();
        _eventsByProcess.Clear();
        _eventsByUser.Clear();
        _eventsByFile.Clear();
        _eventsByNetworkFlow.Clear();
        _eventsByCategory.Clear();
        _eventsByCategoryAndAction.Clear();
        _activeEventIds.Clear();
        Interlocked.Exchange(ref _eventCount, 0);
        Interlocked.Exchange(ref _evictedEvents, 0);
        Interlocked.Exchange(ref _droppedEvents, 0);
    }

    private void AddToIndex<TKey>(
        ConcurrentDictionary<TKey, ConcurrentQueue<IndexedEvent>> index,
        TKey key,
        IndexedEvent activityEvent) where TKey : notnull
    {
        var queue = index.GetOrAdd(key, _ => new ConcurrentQueue<IndexedEvent>());
        queue.Enqueue(activityEvent);
        CleanupQueue(queue);
    }

    private void EnforceCapacity()
    {
        if (_maxEvents == 0)
            return;

        var batch = new List<IndexedEvent>(EvictionBatchSize);
        while (Interlocked.Read(ref _eventCount) > _maxEvents)
        {
            var toRemove = (int)Math.Min(EvictionBatchSize, Interlocked.Read(ref _eventCount) - _maxEvents);
            if (toRemove <= 0)
                break;

            batch.Clear();
            while (batch.Count < toRemove && _allEvents.TryDequeue(out var evicted))
                batch.Add(evicted);

            if (batch.Count == 0)
                break;

            var removed = 0;
            foreach (var evicted in batch)
            {
                if (_activeEventIds.TryRemove(evicted.Id, out _))
                    removed++;
            }
            Interlocked.Add(ref _eventCount, -removed);
            Interlocked.Add(ref _evictedEvents, removed);
        }
    }

    /// <summary>
    /// Trims inactive (evicted) entries from all secondary index queues to reclaim memory.
    /// Call periodically when EvictedEvents &gt; 0 (e.g. from UI timer).
    /// </summary>
    public void TrimAllQueues()
    {
        foreach (var queue in _eventsByProcess.Values)
            CleanupQueue(queue);
        foreach (var queue in _eventsByUser.Values)
            CleanupQueue(queue);
        foreach (var queue in _eventsByFile.Values)
            CleanupQueue(queue);
        foreach (var queue in _eventsByNetworkFlow.Values)
            CleanupQueue(queue);
        foreach (var queue in _eventsByCategory.Values)
            CleanupQueue(queue);
        foreach (var queue in _eventsByCategoryAndAction.Values)
            CleanupQueue(queue);
    }

    private IEnumerable<ActivityEvent> FilterActive(ConcurrentQueue<IndexedEvent> queue)
    {
        CleanupQueue(queue);
        return queue.Where(e => IsActive(e.Id)).Select(e => e.Event);
    }

    private void CleanupQueue(ConcurrentQueue<IndexedEvent> queue)
    {
        while (queue.TryPeek(out var peek) && !IsActive(peek.Id))
        {
            queue.TryDequeue(out _);
        }
    }

    private bool IsActive(long id)
    {
        return _activeEventIds.ContainsKey(id);
    }

    // Legacy methods for backward compatibility
    [Obsolete("Use GetEventsByNetworkFlow instead")]
    public IEnumerable<ActivityEvent> GetEventsByNetwork(NetworkKey networkKey)
    {
        // Try to find matching NetworkFlowKey (approximate match)
        return _eventsByNetworkFlow.Values
            .SelectMany(events => FilterActive(events))
            .Where(e => e.ObjectNetworkFlow.HasValue &&
                       e.ObjectNetworkFlow.Value.Protocol == networkKey.Protocol);
    }

    [Obsolete("Use GetEventsByCategoryAndAction instead")]
    public IEnumerable<ActivityEvent> GetEventsByType(string eventType)
    {
        // Try to parse as Category.Action or just Category
        var parts = eventType.Split('.');
        if (parts.Length == 2)
            return GetEventsByCategoryAndAction(parts[0], parts[1]);
        return GetEventsByCategory(eventType);
    }

    [Obsolete("Use GetEventsByField instead")]
    public IEnumerable<ActivityEvent> GetEventsByProperty(string propertyName, object? propertyValue)
    {
        return GetEventsByField(propertyName, propertyValue);
    }

    private sealed record IndexedEvent(long Id, ActivityEvent Event);
}
