using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Settings;

namespace WinDFIR.UI.ViewModels;

/// <summary>
/// View model for Timeline view with unified activity event timeline.
/// </summary>
public class TimelineViewModel : BaseViewModel
{
    private IActivityIndex _index;
    private DateTime? _startTime;
    private DateTime? _endTime;
    private string _filterText = string.Empty;
    private string _filterCategory = string.Empty;
    private string _filterAction = string.Empty;
    private string _filterProcessName = string.Empty;
    private string _filterPath = string.Empty;

    public TimelineViewModel(IActivityIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        Events = new ObservableCollection<ActivityEvent>();
        var settings = HostWitnessSettings.Load();
        _useUtcDisplay = string.Equals(settings.Ui?.TimeZoneDisplay?.Trim(), "UTC", StringComparison.OrdinalIgnoreCase);
    }

    private readonly bool _useUtcDisplay;

    /// <summary>When true, timestamps are shown in UTC; otherwise in local time.</summary>
    public bool UseUtcDisplay => _useUtcDisplay;

    public ObservableCollection<ActivityEvent> Events { get; }

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
                Refresh();
        }
    }

    /// <summary>Filter by Category (e.g. File, Registry). Empty = no filter.</summary>
    public string FilterCategory
    {
        get => _filterCategory;
        set
        {
            if (SetProperty(ref _filterCategory, value ?? string.Empty))
                Refresh();
        }
    }

    /// <summary>Filter by Action (e.g. Create, Write). Empty = no filter.</summary>
    public string FilterAction
    {
        get => _filterAction;
        set
        {
            if (SetProperty(ref _filterAction, value ?? string.Empty))
                Refresh();
        }
    }

    /// <summary>Filter by process name / SubjectProcess string. Empty = no filter.</summary>
    public string FilterProcessName
    {
        get => _filterProcessName;
        set
        {
            if (SetProperty(ref _filterProcessName, value ?? string.Empty))
                Refresh();
        }
    }

    /// <summary>Filter by path (ObjectFile path or Summary). Empty = no filter.</summary>
    public string FilterPath
    {
        get => _filterPath;
        set
        {
            if (SetProperty(ref _filterPath, value ?? string.Empty))
                Refresh();
        }
    }

    private ActivityEvent? _selectedEvent;
    private ProcessKey? _filterByProcessKey;

    public ActivityEvent? SelectedEvent
    {
        get => _selectedEvent;
        set => SetProperty(ref _selectedEvent, value);
    }

    /// <summary>When set, timeline shows only events for this process (drill-down from Live Process).</summary>
    public ProcessKey? FilterByProcessKey
    {
        get => _filterByProcessKey;
        set => SetProperty(ref _filterByProcessKey, value);
    }

    /// <summary>Display label when FilterByProcessKey is set (e.g. "Filtered by PID: 1234").</summary>
    public string? ProcessFilterLabel =>
        _filterByProcessKey.HasValue ? $"Filtered by PID: {_filterByProcessKey.Value.ProcessId}" : null;

    public bool HasProcessFilter => _filterByProcessKey.HasValue;

    public void Refresh()
    {
        // Capture selection
        var currentSelection = _selectedEvent;
        Func<ActivityEvent, bool>? isMatch = null;
        if (currentSelection != null)
        {
            var t = currentSelection.Timestamp;
            var s = currentSelection.Summary;
            isMatch = e => e.Timestamp == t && e.Summary == s;
        }

        Events.Clear();

        var start = _startTime ?? DateTime.MinValue;
        var end = _endTime ?? DateTime.MaxValue;

        IEnumerable<ActivityEvent> events;
        if (_filterByProcessKey.HasValue)
        {
            events = _index.GetEventsByProcess(_filterByProcessKey.Value);
            // Optionally restrict to time range
            events = events.Where(e => e.Timestamp >= start && e.Timestamp <= end);
        }
        else
        {
            events = _index.GetEventsByTimeRange(start, end);
        }

        // Apply full-text search if provided
        if (!string.IsNullOrWhiteSpace(_filterText))
        {
            var filterLower = _filterText.ToLowerInvariant();
            events = events.Where(e =>
                e.Summary?.ToLowerInvariant().Contains(filterLower) == true ||
                e.Category.ToLowerInvariant().Contains(filterLower) ||
                e.Action.ToLowerInvariant().Contains(filterLower) ||
                e.Fields.Values.Any(v => v?.ToString()?.ToLowerInvariant().Contains(filterLower) == true));
        }

        // Apply Category filter
        if (!string.IsNullOrWhiteSpace(_filterCategory))
        {
            var cat = _filterCategory.Trim().ToLowerInvariant();
            events = events.Where(e => e.Category?.ToLowerInvariant().Contains(cat) == true);
        }

        // Apply Action filter
        if (!string.IsNullOrWhiteSpace(_filterAction))
        {
            var act = _filterAction.Trim().ToLowerInvariant();
            events = events.Where(e => e.Action?.ToLowerInvariant().Contains(act) == true);
        }

        // Apply Process name filter (SubjectProcess string or Summary)
        if (!string.IsNullOrWhiteSpace(_filterProcessName))
        {
            var pn = _filterProcessName.Trim().ToLowerInvariant();
            events = events.Where(e =>
                (e.SubjectProcess.HasValue && e.SubjectProcess.Value.ToString().ToLowerInvariant().Contains(pn)) ||
                (e.Summary?.ToLowerInvariant().Contains(pn) == true));
        }

        // Apply Path filter (ObjectFile path or Summary)
        if (!string.IsNullOrWhiteSpace(_filterPath))
        {
            var path = _filterPath.Trim().ToLowerInvariant();
            events = events.Where(e =>
                (e.ObjectFile.HasValue && e.ObjectFile.Value.Path?.ToLowerInvariant().Contains(path) == true) ||
                (e.ObjectFile.HasValue && e.ObjectFile.Value.ToString().ToLowerInvariant().Contains(path)) ||
                (e.Summary?.ToLowerInvariant().Contains(path) == true));
        }

        // Order by timestamp desc (History usually newest first, LastActivityView is newest first)
        var sortedEvents = events.OrderByDescending(e => e.Timestamp).ToList();

        ActivityEvent? newSelection = null;

        foreach (var evt in sortedEvents)
        {
            Events.Add(evt);
            if (isMatch != null && newSelection == null && isMatch(evt))
            {
                newSelection = evt;
            }
        }

        if (newSelection != null)
        {
            SelectedEvent = newSelection;
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>True when current filter result has no events. For empty-state UI.</summary>
    public bool IsEmpty => Events.Count == 0;

    public void ClearFilters()
    {
        StartTime = null;
        EndTime = null;
        FilterText = string.Empty;
        FilterCategory = string.Empty;
        FilterAction = string.Empty;
        FilterProcessName = string.Empty;
        FilterPath = string.Empty;
        Refresh();
    }

    /// <summary>Clear process drill-down filter and refresh.</summary>
    public void ClearProcessFilter()
    {
        if (!_filterByProcessKey.HasValue)
            return;
        _filterByProcessKey = null;
        OnPropertyChanged(nameof(FilterByProcessKey));
        OnPropertyChanged(nameof(ProcessFilterLabel));
        OnPropertyChanged(nameof(HasProcessFilter));
        Refresh();
    }

    /// <summary>Apply drill-down filter by process (e.g. from Live Process selection).</summary>
    public void ApplyProcessFilter(ProcessKey processKey)
    {
        _filterByProcessKey = processKey;
        OnPropertyChanged(nameof(FilterByProcessKey));
        OnPropertyChanged(nameof(ProcessFilterLabel));
        OnPropertyChanged(nameof(HasProcessFilter));
        Refresh();
    }

    /// <summary>Switch the timeline to use a different index (e.g. loaded snapshot). Call Refresh after if needed.</summary>
    public void SetIndex(IActivityIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        Refresh();
    }

    /// <summary>Set time range to today (local midnight to end of day).</summary>
    public void SetTimeRangeToday()
    {
        var now = DateTime.Now;
        StartTime = now.Date;
        EndTime = now.Date.AddDays(1).AddTicks(-1);
    }

    /// <summary>Set time range to last 24 hours from now.</summary>
    public void SetTimeRangeLast24Hours()
    {
        var now = DateTime.Now;
        EndTime = now;
        StartTime = now.AddHours(-24);
    }

    /// <summary>Set time range to last 7 days from now.</summary>
    public void SetTimeRangeLast7Days()
    {
        var now = DateTime.Now;
        EndTime = now;
        StartTime = now.AddDays(-7);
    }

    /// <summary>Suggested export filename including date range when StartTime/EndTime are set (e.g. timeline_2025-03-01_2025-03-05.csv).</summary>
    public string GetSuggestedExportFileName(string extension)
    {
        var ext = extension?.TrimStart('.').ToLowerInvariant() ?? "csv";
        if (_startTime.HasValue && _endTime.HasValue)
        {
            var from = _startTime.Value.ToString("yyyy-MM-dd");
            var to = _endTime.Value.ToString("yyyy-MM-dd");
            return $"timeline_{from}_{to}.{ext}";
        }
        return $"timeline_export.{ext}";
    }

    /// <summary>Export current Events (filtered list) to CSV. Returns error message or null on success.</summary>
    public string? ExportToCsv(string filePath)
    {
        try
        {
            using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
            if (_startTime.HasValue && _endTime.HasValue)
                writer.WriteLine("# TimeRange,{0:yyyy-MM-dd HH:mm:ss},{1:yyyy-MM-dd HH:mm:ss}", _startTime.Value, _endTime.Value);
            writer.WriteLine("Timestamp,Category,Action,Summary,SubjectProcess,SubjectUser,ObjectFile,ObjectUrl,Confidence");
            foreach (var e in Events)
            {
                writer.WriteLine(string.Join(",",
                    CsvEscape(e.Timestamp.ToString("O")),
                    CsvEscape(e.Category),
                    CsvEscape(e.Action),
                    CsvEscape(e.Summary ?? ""),
                    CsvEscape(e.SubjectProcess?.ToString() ?? ""),
                    CsvEscape(e.SubjectUser?.ToString() ?? ""),
                    CsvEscape(e.ObjectFile?.ToString() ?? ""),
                    CsvEscape(e.ObjectUrl ?? ""),
                    CsvEscape(e.Confidence ?? "")));
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string CsvEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    /// <summary>Export current Events to JSON. Returns error message or null on success.</summary>
    public string? ExportToJson(string filePath)
    {
        try
        {
            var meta = _startTime.HasValue && _endTime.HasValue
                ? new { timeRangeStart = _startTime.Value.ToString("O"), timeRangeEnd = _endTime.Value.ToString("O") }
                : (object?)null;
            var list = Events.Select(e => new
            {
                timestamp = e.Timestamp.ToString("O"),
                category = e.Category,
                action = e.Action,
                summary = e.Summary,
                subjectProcess = e.SubjectProcess?.ToString(),
                subjectUser = e.SubjectUser?.ToString(),
                objectFile = e.ObjectFile?.ToString(),
                objectUrl = e.ObjectUrl,
                confidence = e.Confidence
            }).ToList();
            var root = meta != null ? new { meta, events = list } : (object)list;
            var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json, Encoding.UTF8);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public void ClearView()
    {
        _startTime = null;
        _endTime = null;
        _filterText = string.Empty;
        _filterCategory = string.Empty;
        _filterAction = string.Empty;
        _filterProcessName = string.Empty;
        _filterPath = string.Empty;
        _filterByProcessKey = null;
        SelectedEvent = null;
        Events.Clear();
        OnPropertyChanged(nameof(StartTime));
        OnPropertyChanged(nameof(EndTime));
        OnPropertyChanged(nameof(FilterText));
        OnPropertyChanged(nameof(FilterCategory));
        OnPropertyChanged(nameof(FilterAction));
        OnPropertyChanged(nameof(FilterProcessName));
        OnPropertyChanged(nameof(FilterPath));
        OnPropertyChanged(nameof(FilterByProcessKey));
        OnPropertyChanged(nameof(ProcessFilterLabel));
        OnPropertyChanged(nameof(HasProcessFilter));
    }
}
