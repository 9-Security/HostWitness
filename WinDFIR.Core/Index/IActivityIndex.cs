using WinDFIR.Core.Entities;

namespace WinDFIR.Core.Index;

/// <summary>
/// Index interface for storing and querying normalized ActivityEvents.
/// Provides correlation capabilities across different event types.
/// Per specification: indexes by ProcessKey, UserKey, FileKey, Time range.
/// </summary>
public interface IActivityIndex
{
    /// <summary>
    /// Adds an activity event to the index.
    /// </summary>
    void AddEvent(ActivityEvent activityEvent);

    /// <summary>
    /// Retrieves all events for a given process key.
    /// </summary>
    IEnumerable<ActivityEvent> GetEventsByProcess(ProcessKey processKey);

    /// <summary>
    /// Retrieves all events for a given user key.
    /// </summary>
    IEnumerable<ActivityEvent> GetEventsByUser(UserKey userKey);

    /// <summary>
    /// Retrieves all events for a given file key.
    /// </summary>
    IEnumerable<ActivityEvent> GetEventsByFile(FileKey fileKey);

    /// <summary>
    /// Retrieves all events for a given network flow key.
    /// </summary>
    IEnumerable<ActivityEvent> GetEventsByNetworkFlow(NetworkFlowKey networkFlowKey);

    /// <summary>
    /// Retrieves all events within a time range.
    /// </summary>
    IEnumerable<ActivityEvent> GetEventsByTimeRange(DateTime start, DateTime end);

    /// <summary>
    /// Retrieves all events by category and action.
    /// </summary>
    IEnumerable<ActivityEvent> GetEventsByCategoryAndAction(string category, string action);

    /// <summary>
    /// Retrieves all events of a specific category.
    /// </summary>
    IEnumerable<ActivityEvent> GetEventsByCategory(string category);

    /// <summary>
    /// Retrieves all events matching a field filter.
    /// </summary>
    IEnumerable<ActivityEvent> GetEventsByField(string fieldName, object? fieldValue);

    /// <summary>
    /// Clears all indexed events.
    /// </summary>
    void Clear();

    // Legacy methods for backward compatibility
    [Obsolete("Use GetEventsByNetworkFlow instead")]
    IEnumerable<ActivityEvent> GetEventsByNetwork(NetworkKey networkKey);

    [Obsolete("Use GetEventsByCategoryAndAction instead")]
    IEnumerable<ActivityEvent> GetEventsByType(string eventType);

    [Obsolete("Use GetEventsByField instead")]
    IEnumerable<ActivityEvent> GetEventsByProperty(string propertyName, object? propertyValue);
}
