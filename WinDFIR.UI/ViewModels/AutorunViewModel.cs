using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;

namespace WinDFIR.UI.ViewModels;

/// <summary>
/// View model for Autorun (Registry Run/RunOnce) view.
/// Displays entries from Offline Hive Registry Run, RunOnce, User Run, User RunOnce, StartupApproved\Run, etc.
/// Aligns with Sysinternals Autoruns "Logon" / Registry locations.
/// </summary>
public class AutorunViewModel : BaseViewModel
{
    private readonly IActivityIndex _index;
    private static readonly HashSet<string> AutorunQueryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Run",
        "RunOnce",
        "RunServices",
        "RunServicesOnce",
        "PoliciesRun",
        "StartupApprovedRun",
        "User Run",
        "User RunOnce",
        "Winlogon",
        "IFEO"
    };

    private string _locationFilter = "All";
    private string _searchText = string.Empty;
    private int _refreshing;
    private bool _refreshPending;
    private AutorunEntry? _selectedEntry;

    public AutorunViewModel(IActivityIndex index)
    {
        _index = index;
        Entries = new ObservableCollection<AutorunEntry>();
        LocationFilterOptions = new List<string> { "All", "Run", "RunOnce", "User Run", "User RunOnce", "RunServices", "RunServicesOnce", "PoliciesRun", "StartupApprovedRun", "Winlogon", "IFEO" };
    }

    public ObservableCollection<AutorunEntry> Entries { get; }
    public IReadOnlyList<string> LocationFilterOptions { get; }

    /// <summary>Currently selected entry; bound to DataGrid SelectedItem. Preserved across refresh so Entry Details stay visible.</summary>
    public AutorunEntry? SelectedEntry
    {
        get => _selectedEntry;
        set => SetProperty(ref _selectedEntry, value);
    }

    public string LocationFilter
    {
        get => _locationFilter;
        set
        {
            if (SetProperty(ref _locationFilter, value))
                Refresh();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                Refresh();
        }
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
                var locationFilter = _locationFilter;
                var searchText = _searchText ?? string.Empty;

                var entries = await Task.Run(() =>
                {
                    var events = _index.GetEventsByCategory("Registry")
                        .Where(e => e.Fields.TryGetValue("QueryName", out var qn) && qn != null && AutorunQueryNames.Contains(qn.ToString()!));

                    if (!string.Equals(locationFilter, "All", StringComparison.OrdinalIgnoreCase))
                    {
                        events = events.Where(e =>
                            e.Fields.TryGetValue("QueryName", out var qn) &&
                            string.Equals(qn?.ToString(), locationFilter, StringComparison.OrdinalIgnoreCase));
                    }

                    if (!string.IsNullOrWhiteSpace(searchText))
                    {
                        var lower = searchText.ToLowerInvariant();
                        events = events.Where(e =>
                        {
                            if (e.Summary?.ToLowerInvariant().Contains(lower) == true) return true;
                            if (e.Fields.TryGetValue("ValueName", out var vn) && vn?.ToString()?.ToLowerInvariant().Contains(lower) == true) return true;
                            if (e.Fields.TryGetValue("ValueData", out var vd) && vd?.ToString()?.ToLowerInvariant().Contains(lower) == true) return true;
                            if (e.Fields.TryGetValue("KeyPath", out var kp) && kp?.ToString()?.ToLowerInvariant().Contains(lower) == true) return true;
                            if (e.Fields.TryGetValue("HiveName", out var hn) && hn?.ToString()?.ToLowerInvariant().Contains(lower) == true) return true;
                            return false;
                        });
                    }

                    return events
                        .OrderBy(e => e.Fields.TryGetValue("QueryName", out var q) ? q?.ToString() ?? "" : "")
                        .ThenBy(e => e.Fields.TryGetValue("ValueName", out var v) ? v?.ToString() ?? "" : "")
                        .ThenByDescending(e => e.Timestamp)
                        .Select(ToAutorunEntry)
                        .ToList();
                });

                try
                {
                    if (Application.Current?.Dispatcher != null)
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            var prevKey = SelectedEntry != null ? (SelectedEntry.FullKeyPath + "|" + SelectedEntry.Entry) : null;
                            Entries.Clear();
                            foreach (var entry in entries)
                                Entries.Add(entry);
                            if (!string.IsNullOrEmpty(prevKey))
                            {
                                var match = Entries.FirstOrDefault(e => (e.FullKeyPath + "|" + e.Entry) == prevKey);
                                SelectedEntry = match;
                            }
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

    private static AutorunEntry ToAutorunEntry(ActivityEvent evt)
    {
        evt.Fields.TryGetValue("QueryName", out var queryName);
        evt.Fields.TryGetValue("HiveName", out var hiveName);
        evt.Fields.TryGetValue("KeyPath", out var keyPath);
        evt.Fields.TryGetValue("ValueName", out var valueName);
        evt.Fields.TryGetValue("ValueData", out var valueData);
        evt.Fields.TryGetValue("ValueType", out var valueType);

        var location = queryName?.ToString() ?? "Registry";
        var hive = hiveName?.ToString() ?? "";
        var fullKey = string.IsNullOrEmpty(hive) ? (keyPath?.ToString() ?? "") : $"{hive}\\{keyPath}";

        return new AutorunEntry
        {
            Location = location,
            Entry = valueName?.ToString() ?? "",
            Command = valueData?.ToString() ?? "",
            LastWriteTime = evt.Timestamp,
            Hive = hive,
            KeyPath = keyPath?.ToString() ?? "",
            ValueType = valueType?.ToString() ?? "",
            FullKeyPath = fullKey
        };
    }

    public void ClearFilters()
    {
        LocationFilter = "All";
        SearchText = string.Empty;
        Refresh();
    }
}

/// <summary>
/// Single autorun registry entry for display (Run/RunOnce value).
/// </summary>
public class AutorunEntry
{
    public string Location { get; set; } = "";
    public string Entry { get; set; } = "";
    public string Command { get; set; } = "";
    public DateTime LastWriteTime { get; set; }
    public string Hive { get; set; } = "";
    public string KeyPath { get; set; } = "";
    public string ValueType { get; set; } = "";
    public string FullKeyPath { get; set; } = "";
}
