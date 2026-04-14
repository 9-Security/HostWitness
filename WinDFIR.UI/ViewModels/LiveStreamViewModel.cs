using System.Collections.Generic;
using System.Collections.ObjectModel;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;

namespace WinDFIR.UI.ViewModels;

/// <summary>
/// View model for Live Stream view with real-time event streaming and pause/resume.
/// </summary>
public class LiveStreamViewModel : BaseViewModel
{
    private readonly IActivityIndex _index;
    private bool _isPaused;
    private readonly ObservableCollection<ActivityEvent> _streamEvents;
    private readonly HashSet<string> _seenEventKeys = new();

    public LiveStreamViewModel(IActivityIndex index)
    {
        _index = index;
        _streamEvents = new ObservableCollection<ActivityEvent>();
    }

    public ObservableCollection<ActivityEvent> StreamEvents => _streamEvents;

    public bool IsPaused
    {
        get => _isPaused;
        set => SetProperty(ref _isPaused, value);
    }

    public int EventCount => _streamEvents.Count;

    public void AddEvent(ActivityEvent activityEvent)
    {
        if (_isPaused)
            return;

        // Create unique key for event to detect duplicates
        var eventKey = BuildEventKey(activityEvent);
        
        if (_seenEventKeys.Contains(eventKey))
            return;

        _seenEventKeys.Add(eventKey);
        _streamEvents.Insert(0, activityEvent); // Insert at top for newest first

        // Limit to last 1000 events for performance
        if (_streamEvents.Count > 1000)
        {
            var oldest = _streamEvents[_streamEvents.Count - 1];
            var oldestKey = BuildEventKey(oldest);
            _seenEventKeys.Remove(oldestKey);
            _streamEvents.RemoveAt(_streamEvents.Count - 1);
        }

        OnPropertyChanged(nameof(EventCount));
    }

    public void Clear()
    {
        _streamEvents.Clear();
        _seenEventKeys.Clear();
        OnPropertyChanged(nameof(EventCount));
    }

    public void Pause()
    {
        IsPaused = true;
    }

    public void Resume()
    {
        IsPaused = false;
    }

    private static string BuildEventKey(ActivityEvent activityEvent)
    {
        activityEvent.Fields.TryGetValue("RecordId", out var recordId);
        return $"{activityEvent.Category}.{activityEvent.Action}.{activityEvent.Timestamp:O}.{activityEvent.Summary}.{recordId}";
    }
}
