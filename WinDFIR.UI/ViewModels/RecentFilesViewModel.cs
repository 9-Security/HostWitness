using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;

namespace WinDFIR.UI.ViewModels;

public class RecentFileItem
{
    public DateTime Timestamp { get; set; }
    public string Source { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public int? MruOrder { get; set; }
    public ActivityEvent SourceEvent { get; set; } = null!;
}

public class RecentFilesViewModel : BaseViewModel
{
    private readonly IActivityIndex _index;
    private DateTime? _startTime;
    private DateTime? _endTime;
    private string _filterText = string.Empty;
    private int _refreshing;
    private bool _refreshPending;

    public RecentFilesViewModel(IActivityIndex index)
    {
        _index = index;
        RecentFiles = new ObservableCollection<RecentFileItem>();
    }

    public ObservableCollection<RecentFileItem> RecentFiles { get; }

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

    private RecentFileItem? _selectedItem;
    public RecentFileItem? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
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
                var currentSelection = _selectedItem;
                var start = _startTime ?? DateTime.MinValue;
                var end = _endTime ?? DateTime.MaxValue;
                var filterText = _filterText;
                var hasSelection = currentSelection != null;
                var selTime = currentSelection?.Timestamp ?? DateTime.MinValue;
                var selSource = currentSelection?.Source;
                var selPath = currentSelection?.Path;

                var items = await Task.Run(() =>
                {
                    var events = _index.GetEventsByTimeRange(start, end)
                        .Where(IsRecentFileEvent);

                    if (!string.IsNullOrWhiteSpace(filterText))
                    {
                        var filter = filterText.ToLowerInvariant();
                        events = events.Where(e =>
                            e.Summary?.ToLowerInvariant().Contains(filter) == true ||
                            e.Fields.Values.Any(v => v?.ToString()?.ToLowerInvariant().Contains(filter) == true));
                    }

                    return events
                        .Select(ToRecentFileItem)
                        .Where(i => i != null)
                        .Select(i => i!)
                        .OrderByDescending(i => i.Timestamp)
                        .ToList();
                });

                try
                {
                    if (Application.Current?.Dispatcher != null)
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            RecentFiles.Clear();
                            RecentFileItem? newSelection = null;
                            foreach (var item in items)
                            {
                                RecentFiles.Add(item);
                                if (hasSelection && newSelection == null &&
                                    item.Timestamp == selTime && item.Source == selSource && item.Path == selPath)
                                    newSelection = item;
                            }
                            if (newSelection != null)
                                SelectedItem = newSelection;
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

    private static bool IsRecentFileEvent(ActivityEvent e)
    {
        if (e.Fields.TryGetValue("Parser", out var parser) && parser?.ToString() == "RecentDocs")
            return true;

        if (e.Fields.ContainsKey("LnkFilePath") || e.Fields.ContainsKey("TargetPath"))
            return true;

        if (e.Fields.ContainsKey("JumpListType") || e.Fields.ContainsKey("AppId"))
            return true;

        return false;
    }

    private static RecentFileItem? ToRecentFileItem(ActivityEvent e)
    {
        if (e.Fields.TryGetValue("Parser", out var parser) && parser?.ToString() == "RecentDocs")
        {
            var path = GetFieldString(e, "ParsedPath");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = GetFieldString(e, "ValueData");
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                path = GetFieldString(e, "RawHex");
            }

            var fileName = GetFieldString(e, "FileName");
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = TryGetFileName(path);
            }
            var extension = GetFieldString(e, "RecentDocsExtension");
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = Path.GetExtension(fileName);
            }

            return new RecentFileItem
            {
                Timestamp = e.Timestamp,
                Source = "RecentDocs MRU",
                FileName = fileName,
                Path = path,
                Extension = extension,
                Details = GetFieldString(e, "ValueName"),
                MruOrder = GetFieldInt(e, "MruOrder"),
                SourceEvent = e
            };
        }

        if (e.Fields.ContainsKey("LnkFilePath") || e.Fields.ContainsKey("TargetPath"))
        {
            var path = GetFieldString(e, "TargetPath");
            return new RecentFileItem
            {
                Timestamp = e.Timestamp,
                Source = "Recent LNK",
                FileName = GetFieldString(e, "TargetFileName"),
                Path = path,
                Extension = Path.GetExtension(path),
                Details = GetFieldString(e, "LnkFilePath"),
                SourceEvent = e
            };
        }

        if (e.Fields.ContainsKey("JumpListType") || e.Fields.ContainsKey("AppId"))
        {
            var type = GetFieldString(e, "JumpListType");
            var appId = GetFieldString(e, "AppId");
            var filePath = GetFieldString(e, "TargetPath");
            if (string.IsNullOrWhiteSpace(filePath))
            {
                filePath = GetFieldString(e, "DestListPathHint");
            }
            if (string.IsNullOrWhiteSpace(filePath))
            {
                filePath = GetFieldString(e, "LinkRelativePath");
            }
            if (string.IsNullOrWhiteSpace(filePath))
            {
                filePath = GetFieldString(e, "LinkNameString");
            }
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }
            return new RecentFileItem
            {
                Timestamp = e.Timestamp,
                Source = string.IsNullOrWhiteSpace(type) ? "JumpList" : $"JumpList {type}",
                FileName = TryGetFileName(filePath),
                Path = filePath,
                Extension = Path.GetExtension(filePath),
                Details = appId,
                SourceEvent = e
            };
        }

        return null;
    }

    private static string GetFieldString(ActivityEvent e, string key)
    {
        return e.Fields.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
    }

    private static int? GetFieldInt(ActivityEvent e, string key)
    {
        if (!e.Fields.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is int i) return i;
        if (value is long l) return (int)l;
        if (int.TryParse(value.ToString(), out var parsed)) return parsed;
        return null;
    }

    private static string TryGetFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        try
        {
            return System.IO.Path.GetFileName(path);
        }
        catch
        {
            return path;
        }
    }
}
