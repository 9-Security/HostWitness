using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;

namespace WinDFIR.UI.ViewModels;

/// <summary>
/// View model for Browsing History view with browser-specific event filtering.
/// </summary>
public class BrowsingHistoryViewModel : BaseViewModel
{
    private readonly IActivityIndex _index;
    private DateTime? _startTime;
    private DateTime? _endTime;
    private string _filterText = string.Empty;
    private int _refreshing;
    private bool _refreshPending;

    public BrowsingHistoryViewModel(IActivityIndex index)
    {
        _index = index;
        BrowserEvents = new ObservableCollection<ActivityEvent>();
    }

    public ObservableCollection<ActivityEvent> BrowserEvents { get; }

    public DateTime? StartTime
    {
        get => _startTime;
        set
        {
            if (SetProperty(ref _startTime, value))
            {
                Refresh();
            }
        }
    }

    public DateTime? EndTime
    {
        get => _endTime;
        set
        {
            if (SetProperty(ref _endTime, value))
            {
                Refresh();
            }
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                Refresh();
            }
        }
    }

    private ActivityEvent? _selectedEvent;

    public ActivityEvent? SelectedEvent
    {
        get => _selectedEvent;
        set => SetProperty(ref _selectedEvent, value);
    }

    public void Refresh()
    {
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            _refreshPending = true;
            return;
        }

        try
        {
            do
            {
                _refreshPending = false;
                var currentSelection = _selectedEvent;
                var start = _startTime ?? DateTime.MinValue;
                var end = _endTime ?? DateTime.MaxValue;
                var filterText = _filterText;
                var hasSelection = currentSelection != null;
                var selTime = currentSelection?.Timestamp ?? DateTime.MinValue;
                var selUrl = currentSelection?.ObjectUrl;
                object? selRecordId = null;
                if (currentSelection != null)
                {
                    currentSelection.Fields.TryGetValue("RecordId", out selRecordId);
                }

                var sortedEvents = await Task.Run(() =>
                {
                    var events = _index.GetEventsByTimeRange(start, end)
                        .Where(e => e.Category == "Browser");

                    if (!string.IsNullOrWhiteSpace(filterText))
                    {
                        var filterLower = filterText.ToLowerInvariant();
                        events = events.Where(e =>
                        {
                            if (e.Summary?.ToLowerInvariant().Contains(filterLower) == true)
                                return true;
                            if (e.ObjectUrl?.ToLowerInvariant().Contains(filterLower) == true)
                                return true;
                            if (e.Fields.TryGetValue("Url", out var urlValue) && urlValue?.ToString()?.ToLowerInvariant().Contains(filterLower) == true)
                                return true;
                            if (e.Fields.TryGetValue("Title", out var titleValue) && titleValue?.ToString()?.ToLowerInvariant().Contains(filterLower) == true)
                                return true;
                            return false;
                        });
                    }

                    return events.OrderByDescending(e => e.Timestamp).ToList();
                });

                try
                {
                    if (Application.Current?.Dispatcher != null)
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            BrowserEvents.Clear();
                            ActivityEvent? newSelection = null;
                            foreach (var evt in sortedEvents)
                            {
                                BrowserEvents.Add(evt);
                                if (hasSelection && newSelection == null &&
                                    evt.Timestamp == selTime && evt.ObjectUrl == selUrl &&
                                    (!evt.Fields.TryGetValue("RecordId", out var rid) || Equals(rid, selRecordId)))
                                    newSelection = evt;
                            }
                            if (newSelection != null)
                                SelectedEvent = newSelection;
                        });
                }
                catch (InvalidOperationException) { /* app shutting down */ }
            } while (_refreshPending);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    public void ClearFilters()
    {
        StartTime = null;
        EndTime = null;
        FilterText = string.Empty;
        Refresh();
    }
}
