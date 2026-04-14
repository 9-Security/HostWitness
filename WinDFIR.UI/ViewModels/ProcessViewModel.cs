using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Snapshot;

namespace WinDFIR.UI.ViewModels;

/// <summary>
/// View model for Process view with tree structure and drill-down capabilities.
/// </summary>
public class ProcessViewModel : BaseViewModel
{
    private const string SettingsFileName = "process_view_columns.json";
    private readonly IActivityIndex _index;
    private ProcessKey? _selectedProcess;
    private ObservableCollection<ProcessTreeItem> _processTree = new();
    private ObservableCollection<ProcessTreeItem> _processTreeRoots = new();
    private bool _isCapturePaused;
    private bool _showProcessName = true;
    private bool _showPid = true;
    private bool _showParentPid = true;
    private bool _showUser = true;
    private bool _showIntegrity = true;
    private bool _showStartTime = true;
    private bool _showEvents = true;
    private bool _showImagePath = true;
    private bool _showCompany = true;
    private bool _showHash = true;
    private bool _showOwnerSid;
    private bool _showSessionId = true;
    private bool _showParentImagePath = true;
    private bool _showAuthenticode = true;
    private bool _showLastOperation = true;
    private bool _showLastResult = true;
    private string _filterCombineMode = "AND";
    private bool _showProcessTree;
    private bool _hideOsProcesses;
    private int _visibleProcessCount;
    private List<ProcessTreeItem> _cachedItems = new();
    private static readonly HashSet<string> KnownOsProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "System Idle Process",
        "smss",
        "csrss",
        "wininit",
        "services",
        "lsass",
        "lsm",
        "svchost",
        "winlogon",
        "explorer",
        "spoolsv",
        "dwm",
        "fontdrvhost",
        "sihost",
        "taskhostw",
        "runtimebroker",
        "dllhost",
        "conhost",
        "ctfmon",
        "searchindexer",
        "searchhost",
        "searchui",
        "shellexperiencehost",
        "startmenuexperiencehost",
        "smartscreen",
        "wuauclt",
        "audiodg",
        "securityhealthservice",
        "securityhealthsystray",
        "wudfhost"
    };

    public ProcessViewModel(IActivityIndex index)
    {
        _index = index;
        Filters.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasActiveFilters));
        LoadColumnSettings();
    }

    /// <summary>True when at least one process filter chip should be shown (hides empty filter strip).</summary>
    public bool HasActiveFilters => Filters.Count > 0;

    public ObservableCollection<ProcessTreeItem> ProcessTree
    {
        get => _processTree;
        set => SetProperty(ref _processTree, value);
    }

    public ObservableCollection<ProcessTreeItem> ProcessTreeRoots
    {
        get => _processTreeRoots;
        set => SetProperty(ref _processTreeRoots, value);
    }

    public ProcessKey? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            SetProperty(ref _selectedProcess, value);
        }
    }

    public HashSet<ProcessKey> HighlightedProcesses { get; } = new();
    public ObservableCollection<ProcessFilterRule> Filters { get; } = new();

    public bool IsCapturePaused
    {
        get => _isCapturePaused;
        set => SetProperty(ref _isCapturePaused, value);
    }

    public bool ShowProcessName
    {
        get => _showProcessName;
        set { if (SetProperty(ref _showProcessName, value)) SaveColumnSettings(); }
    }

    public bool ShowPid
    {
        get => _showPid;
        set { if (SetProperty(ref _showPid, value)) SaveColumnSettings(); }
    }

    public bool ShowParentPid
    {
        get => _showParentPid;
        set { if (SetProperty(ref _showParentPid, value)) SaveColumnSettings(); }
    }

    public bool ShowUser
    {
        get => _showUser;
        set { if (SetProperty(ref _showUser, value)) SaveColumnSettings(); }
    }

    public bool ShowIntegrity
    {
        get => _showIntegrity;
        set { if (SetProperty(ref _showIntegrity, value)) SaveColumnSettings(); }
    }

    public bool ShowStartTime
    {
        get => _showStartTime;
        set { if (SetProperty(ref _showStartTime, value)) SaveColumnSettings(); }
    }

    public bool ShowEvents
    {
        get => _showEvents;
        set { if (SetProperty(ref _showEvents, value)) SaveColumnSettings(); }
    }

    public bool ShowImagePath
    {
        get => _showImagePath;
        set { if (SetProperty(ref _showImagePath, value)) SaveColumnSettings(); }
    }

    public bool ShowCompany
    {
        get => _showCompany;
        set { if (SetProperty(ref _showCompany, value)) SaveColumnSettings(); }
    }

    public bool ShowHash
    {
        get => _showHash;
        set { if (SetProperty(ref _showHash, value)) SaveColumnSettings(); }
    }

    public bool ShowOwnerSid
    {
        get => _showOwnerSid;
        set { if (SetProperty(ref _showOwnerSid, value)) SaveColumnSettings(); }
    }

    public bool ShowSessionId
    {
        get => _showSessionId;
        set { if (SetProperty(ref _showSessionId, value)) SaveColumnSettings(); }
    }

    public bool ShowParentImagePath
    {
        get => _showParentImagePath;
        set { if (SetProperty(ref _showParentImagePath, value)) SaveColumnSettings(); }
    }

    public bool ShowAuthenticode
    {
        get => _showAuthenticode;
        set { if (SetProperty(ref _showAuthenticode, value)) SaveColumnSettings(); }
    }

    public bool ShowLastOperation
    {
        get => _showLastOperation;
        set { if (SetProperty(ref _showLastOperation, value)) SaveColumnSettings(); }
    }

    public bool ShowLastResult
    {
        get => _showLastResult;
        set { if (SetProperty(ref _showLastResult, value)) SaveColumnSettings(); }
    }

    public string FilterCombineMode
    {
        get => _filterCombineMode;
        set => SetProperty(ref _filterCombineMode, value);
    }

    public bool ShowProcessTree
    {
        get => _showProcessTree;
        set
        {
            if (SetProperty(ref _showProcessTree, value))
            {
                RebuildFromCache();
            }
        }
    }

    public bool HideOsProcesses
    {
        get => _hideOsProcesses;
        set
        {
            if (SetProperty(ref _hideOsProcesses, value))
            {
                RebuildFromCache();
            }
        }
    }

    public int VisibleProcessCount
    {
        get => _visibleProcessCount;
        private set => SetProperty(ref _visibleProcessCount, value);
    }

    public void Refresh(bool force = false)
    {
        if (IsCapturePaused && !force)
            return;

        // Build process tree from indexed events
        var processKeys = new HashSet<ProcessKey>();
        var processEvents = _index.GetEventsByCategory("Process").ToList();

        foreach (var evt in processEvents)
        {
            if (evt.SubjectProcess.HasValue)
            {
                processKeys.Add(evt.SubjectProcess.Value);
            }
        }

        // Clear existing items and add new ones to the same collection
        // This ensures the binding is maintained
        _processTree.Clear();
        _processTreeRoots.Clear();
        
        var items = new List<ProcessTreeItem>();
        foreach (var processKey in processKeys)
        {
            var events = _index.GetEventsByProcess(processKey).ToList();
            var startEvent = events
                .Where(e => e.Action == "Start")
                .OrderBy(e => e.Timestamp)
                .FirstOrDefault();
            var lastEvent = events.OrderByDescending(e => e.Timestamp).FirstOrDefault();
            var parentPid = TryGetParentPid(events);
            var lastOperation = lastEvent?.Fields.GetValueOrDefault("Operation")?.ToString() ?? string.Empty;
            var lastResult = lastEvent?.Fields.GetValueOrDefault("Result")?.ToString() ?? string.Empty;
            
            var item = new ProcessTreeItem
            {
                ProcessKey = processKey,
                ProcessName = FirstFieldValue(startEvent ?? lastEvent, "ProcessName") 
                    ?? $"PID {processKey.ProcessId}",
                CommandLine = startEvent?.Fields.GetValueOrDefault("CommandLine")?.ToString() ?? string.Empty,
                UserName = startEvent?.Fields.GetValueOrDefault("UserName")?.ToString() ?? "Unknown",
                StartTime = processKey.CreateTime,
                EventCount = events.Count(),
                ParentProcessId = parentPid,
                Integrity = startEvent?.Fields.GetValueOrDefault("Integrity")?.ToString() ?? string.Empty,
                ImagePath = FirstFieldValue(startEvent, "ImagePath", "ExecutablePath", "ProcessPath") ?? string.Empty,
                Company = startEvent?.Fields.GetValueOrDefault("Company")?.ToString() ?? string.Empty,
                Hash = startEvent?.Fields.GetValueOrDefault("Hash")?.ToString() ?? string.Empty,
                OwnerSid = startEvent?.Fields.GetValueOrDefault("OwnerSid")?.ToString() ?? string.Empty,
                SessionId = TryGetIntField(startEvent, "SessionId"),
                ParentImagePath = startEvent?.Fields.GetValueOrDefault("ParentImagePath")?.ToString() ?? string.Empty,
                AuthenticodeSummary = BuildAuthenticodeSummary(startEvent),
                LastOperation = lastOperation,
                LastResult = lastResult,
                IsHighlighted = HighlightedProcesses.Contains(processKey)
            };

            items.Add(item);
        }

        _cachedItems = items;
        BuildView(items);

        // Notify that the collection has changed
        OnPropertyChanged(nameof(ProcessTree));
        OnPropertyChanged(nameof(ProcessTreeRoots));
    }

    private void RebuildFromCache()
    {
        if (_cachedItems.Count == 0)
        {
            Refresh(true);
            return;
        }

        _processTree.Clear();
        _processTreeRoots.Clear();
        BuildView(_cachedItems);
        OnPropertyChanged(nameof(ProcessTree));
        OnPropertyChanged(nameof(ProcessTreeRoots));
    }

    private void BuildView(List<ProcessTreeItem> items)
    {
        var filteredItems = ApplyFilters(items).ToList();
        VisibleProcessCount = filteredItems.Count;

        ApplyProcessTree(filteredItems);
        foreach (var item in filteredItems)
        {
            _processTree.Add(item);
        }

        var roots = BuildProcessTree(filteredItems);
        if (roots.Count == 0 && filteredItems.Count > 0)
        {
            roots = filteredItems;
        }
        foreach (var item in roots)
        {
            _processTreeRoots.Add(item);
        }
    }

    public void ApplyDiff(SnapshotDiff diff)
    {
        HighlightedProcesses.Clear();
        foreach (var processKey in diff.NewProcesses)
        {
            HighlightedProcesses.Add(processKey);
        }
        Refresh();
    }

    public void ClearView()
    {
        _processTree.Clear();
        _processTreeRoots.Clear();
        HighlightedProcesses.Clear();
        SelectedProcess = null;
        OnPropertyChanged(nameof(ProcessTree));
        OnPropertyChanged(nameof(ProcessTreeRoots));
    }

    public void AddFilter(ProcessFilterRule rule)
    {
        Filters.Add(rule);
        OnPropertyChanged(nameof(Filters));
        // No related-events view
    }

    public void RemoveFilter(ProcessFilterRule rule)
    {
        Filters.Remove(rule);
        OnPropertyChanged(nameof(Filters));
        // No related-events view
    }

    public void ClearFilters()
    {
        Filters.Clear();
        OnPropertyChanged(nameof(Filters));
        // No related-events view
    }

    private IEnumerable<ProcessTreeItem> ApplyFilters(IEnumerable<ProcessTreeItem> items)
    {
        if (HideOsProcesses)
        {
            items = items.Where(item => !IsOsProcess(item));
        }

        if (Filters.Count == 0)
            return items;

        var includeRules = Filters.Where(r => r.Action == "Include").ToList();
        var excludeRules = Filters.Where(r => r.Action == "Exclude").ToList();
        var useAnd = string.Equals(FilterCombineMode, "AND", StringComparison.OrdinalIgnoreCase);

        return items.Where(item =>
        {
            var includeMatch = includeRules.Count == 0 ||
                               (useAnd ? includeRules.All(r => r.IsMatch(item))
                                       : includeRules.Any(r => r.IsMatch(item)));
            if (!includeMatch)
                return false;

            var excludeMatch = excludeRules.Any(r => r.IsMatch(item));
            return !excludeMatch;
        });
    }


    private static string BuildAuthenticodeSummary(ActivityEvent? evt)
    {
        if (evt == null)
            return string.Empty;

        var st = evt.Fields.GetValueOrDefault("AuthenticodeStatus")?.ToString();
        var pub = evt.Fields.GetValueOrDefault("AuthenticodePublisher")?.ToString();
        if (string.IsNullOrWhiteSpace(st) && string.IsNullOrWhiteSpace(pub))
            return string.Empty;
        if (string.IsNullOrWhiteSpace(pub))
            return st ?? string.Empty;
        if (string.IsNullOrWhiteSpace(st))
            return pub!;

        return $"{st}; {pub}";
    }

    private static int? TryGetIntField(ActivityEvent? evt, params string[] keys)
    {
        if (evt == null)
            return null;

        foreach (var key in keys)
        {
            if (evt.Fields.TryGetValue(key, out var value) && value != null)
            {
                if (TryParsePidValue(value, out var parsed))
                    return parsed;
            }
        }

        return null;
    }

    private static int? TryGetParentPid(IEnumerable<ActivityEvent> events)
    {
        foreach (var evt in events)
        {
            var parent = TryGetIntField(evt, "ParentProcessId", "ParentPID", "ParentPid", "PPID", "ParentProcessID");
            if (parent.HasValue && parent.Value > 0)
                return parent.Value;
        }

        return null;
    }

    private static bool TryParsePidValue(object value, out int parsed)
    {
        parsed = 0;

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(trimmed[2..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out parsed);

        return int.TryParse(trimmed, out parsed);
    }

    private static string? FirstFieldValue(ActivityEvent? evt, params string[] keys)
    {
        if (evt == null)
            return null;

        foreach (var key in keys)
        {
            if (evt.Fields.TryGetValue(key, out var value) && value != null)
            {
                var text = value.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return null;
    }

    private void ApplyProcessTree(List<ProcessTreeItem> items)
    {
        if (items.Count == 0)
            return;

        var byPid = BuildPidMap(items);

        foreach (var item in items)
        {
            item.Children.Clear();
            if (item.ParentProcessId.HasValue && item.ParentProcessId.Value > 0)
            {
                var parentPid = (uint)item.ParentProcessId.Value;
                if (byPid.TryGetValue(parentPid, out var parent))
                {
                item.ParentProcessName = parent.ProcessName;
                    if (parent.ParentProcessId.HasValue && parent.ParentProcessId.Value > 0)
                {
                        var grandParentPid = (uint)parent.ParentProcessId.Value;
                        if (byPid.TryGetValue(grandParentPid, out var grandparent))
                        {
                            item.GrandparentProcessName = grandparent.ProcessName;
                        }
                }
                }
            }
            else
            {
                item.ParentProcessName = string.Empty;
                item.GrandparentProcessName = string.Empty;
            }

            var currentPid = (int)item.ProcessKey.ProcessId;
            var children = items
                .Where(c => c.ParentProcessId == currentPid)
                .Select(c => c.ProcessName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();

            item.ChildProcessNames = children.Count == 0 ? string.Empty : string.Join(", ", children);
        }
    }

    private List<ProcessTreeItem> BuildProcessTree(List<ProcessTreeItem> items)
    {
        foreach (var item in items)
        {
            item.Children.Clear();
            item.ParentProcessName = string.Empty;
            item.GrandparentProcessName = string.Empty;
            item.ChildProcessNames = string.Empty;
        }

        var byPid = BuildPidMap(items);

        var roots = new List<ProcessTreeItem>();
        foreach (var item in items)
        {
            if (item.ParentProcessId.HasValue && item.ParentProcessId.Value > 0)
            {
                var parentPid = (uint)item.ParentProcessId.Value;
                if (byPid.TryGetValue(parentPid, out var parent))
                {
                    parent.Children.Add(item);
                    item.ParentProcessName = parent.ProcessName;
                    if (parent.ParentProcessId.HasValue && parent.ParentProcessId.Value > 0)
                    {
                        var grandParentPid = (uint)parent.ParentProcessId.Value;
                        if (byPid.TryGetValue(grandParentPid, out var grandparent))
                        {
                            item.GrandparentProcessName = grandparent.ProcessName;
                        }
                    }
                    continue;
                }
            }

            roots.Add(item);
        }

        return roots;
    }

    private static bool IsOsProcess(ProcessTreeItem item)
    {
        var name = item.ProcessName;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalized = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;

        if (KnownOsProcesses.Contains(normalized))
            return true;

        if (KnownOsProcesses.Contains(name))
            return true;

        return string.Equals(item.Company, "Microsoft Corporation", StringComparison.OrdinalIgnoreCase) &&
               item.ImagePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<uint, ProcessTreeItem> BuildPidMap(IEnumerable<ProcessTreeItem> items)
    {
        return items
            .Where(i => i.ProcessKey.ProcessId > 0)
            .GroupBy(i => i.ProcessKey.ProcessId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(i => i.ProcessKey.CreateTime).First());
    }

    private void LoadColumnSettings()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var settings = System.Text.Json.JsonSerializer.Deserialize<ProcessViewColumnSettings>(json);
            if (settings == null)
                return;

            _showProcessName = settings.ShowProcessName;
            _showPid = settings.ShowPid;
            _showParentPid = settings.ShowParentPid;
            _showUser = settings.ShowUser;
            _showIntegrity = settings.ShowIntegrity;
            _showStartTime = settings.ShowStartTime;
            _showEvents = settings.ShowEvents;
            _showImagePath = settings.ShowImagePath;
            _showCompany = settings.ShowCompany;
            _showHash = settings.ShowHash;
            _showLastOperation = settings.ShowLastOperation;
            _showLastResult = settings.ShowLastResult;

            GetPhase4ColumnVisibility(settings, out _showOwnerSid, out _showSessionId, out _showParentImagePath,
                out _showAuthenticode);
        }
        catch
        {
            // Ignore settings load issues
        }
    }

    /// <summary>Test seam: Phase4 column defaults when <see cref="ProcessViewColumnSettings.ColumnSettingsVersion"/> &lt; 2.</summary>
    internal static void GetPhase4ColumnVisibility(
        ProcessViewColumnSettings settings,
        out bool showOwnerSid,
        out bool showSessionId,
        out bool showParentImagePath,
        out bool showAuthenticode)
    {
        if (settings.ColumnSettingsVersion >= 2)
        {
            showOwnerSid = settings.ShowOwnerSid;
            showSessionId = settings.ShowSessionId;
            showParentImagePath = settings.ShowParentImagePath;
            showAuthenticode = settings.ShowAuthenticode;
        }
        else
        {
            showOwnerSid = false;
            showSessionId = true;
            showParentImagePath = true;
            showAuthenticode = true;
        }
    }

    private void SaveColumnSettings()
    {
        try
        {
            var path = GetSettingsPath();
            var settings = new ProcessViewColumnSettings
            {
                ColumnSettingsVersion = 2,
                ShowProcessName = ShowProcessName,
                ShowPid = ShowPid,
                ShowParentPid = ShowParentPid,
                ShowUser = ShowUser,
                ShowIntegrity = ShowIntegrity,
                ShowStartTime = ShowStartTime,
                ShowEvents = ShowEvents,
                ShowImagePath = ShowImagePath,
                ShowCompany = ShowCompany,
                ShowHash = ShowHash,
                ShowOwnerSid = ShowOwnerSid,
                ShowSessionId = ShowSessionId,
                ShowParentImagePath = ShowParentImagePath,
                ShowAuthenticode = ShowAuthenticode,
                ShowLastOperation = ShowLastOperation,
                ShowLastResult = ShowLastResult
            };

            var json = System.Text.Json.JsonSerializer.Serialize(settings);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Ignore settings save issues
        }
    }

    private static string GetSettingsPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(root, "HostWitness", SettingsFileName);
    }
}

/// <summary>
/// Tree item for process view.
/// </summary>
public class ProcessTreeItem
{
    public ProcessKey ProcessKey { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string CommandLine { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public int EventCount { get; set; }
    public int? ParentProcessId { get; set; }
    public string ParentProcessName { get; set; } = string.Empty;
    public string GrandparentProcessName { get; set; } = string.Empty;
    public string ChildProcessNames { get; set; } = string.Empty;
    public ObservableCollection<ProcessTreeItem> Children { get; } = new();
    public string Integrity { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string OwnerSid { get; set; } = string.Empty;
    public int? SessionId { get; set; }
    public string ParentImagePath { get; set; } = string.Empty;
    public string AuthenticodeSummary { get; set; } = string.Empty;
    public string LastOperation { get; set; } = string.Empty;
    public string LastResult { get; set; } = string.Empty;
    public bool IsHighlighted { get; set; }
}

public class ProcessFilterRule
{
    public string Action { get; set; } = "Include"; // Include | Exclude
    public string Field { get; set; } = "ProcessName"; // ProcessName | PID | User | CommandLine
    public string Operator { get; set; } = "Contains"; // Contains | Equals | StartsWith | EndsWith
    public string Value { get; set; } = string.Empty;

    public bool IsMatch(ProcessTreeItem item)
    {
        if (string.IsNullOrWhiteSpace(Value))
            return false;

        var fieldValue = Field switch
        {
            "ProcessName" => item.ProcessName,
            "PID" => item.ProcessKey.ProcessId.ToString(),
            "User" => item.UserName,
            "CommandLine" => item.CommandLine,
            "ParentPID" => item.ParentProcessId?.ToString() ?? string.Empty,
            "Integrity" => item.Integrity,
            "ImagePath" => item.ImagePath,
            "Company" => item.Company,
            "Hash" => item.Hash,
            "OwnerSid" => item.OwnerSid,
            "SessionId" => item.SessionId?.ToString() ?? string.Empty,
            "ParentImagePath" => item.ParentImagePath,
            "Authenticode" => item.AuthenticodeSummary,
            "Operation" => item.LastOperation,
            "Result" => item.LastResult,
            "Path" => item.ImagePath,
            "Duration" => string.Empty,
            _ => string.Empty
        };

        return Operator switch
        {
            "Contains" => fieldValue.Contains(Value, StringComparison.OrdinalIgnoreCase),
            "Equals" => string.Equals(fieldValue, Value, StringComparison.OrdinalIgnoreCase),
            "StartsWith" => fieldValue.StartsWith(Value, StringComparison.OrdinalIgnoreCase),
            "EndsWith" => fieldValue.EndsWith(Value, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    // No event-grid filtering; rule applies to process rows only.
}

public class ProcessViewColumnSettings
{
    public int ColumnSettingsVersion { get; set; }
    public bool ShowProcessName { get; set; } = true;
    public bool ShowPid { get; set; } = true;
    public bool ShowParentPid { get; set; } = true;
    public bool ShowUser { get; set; } = true;
    public bool ShowIntegrity { get; set; } = true;
    public bool ShowStartTime { get; set; } = true;
    public bool ShowEvents { get; set; } = true;
    public bool ShowImagePath { get; set; } = true;
    public bool ShowCompany { get; set; } = true;
    public bool ShowHash { get; set; } = true;
    public bool ShowOwnerSid { get; set; }
    public bool ShowSessionId { get; set; } = true;
    public bool ShowParentImagePath { get; set; } = true;
    public bool ShowAuthenticode { get; set; } = true;
    public bool ShowLastOperation { get; set; } = true;
    public bool ShowLastResult { get; set; } = true;
}
