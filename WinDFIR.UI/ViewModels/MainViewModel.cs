using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinDFIR.UI.ViewModels;

/// <summary>
/// Main window state for tab selection and future messaging/decoupling.
/// Reduces coupling in MainWindow.xaml.cs and prepares for independent view windows.
/// Tab order must match MainWindow.xaml TabItem order (dynamic then static when resolving content).
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    /// <summary>Dynamic tab keys in TabControl order: Timeline, LiveStream, Process, LiveTcp.</summary>
    public static readonly IReadOnlyList<string> DynamicTabKeys = new[] { "Timeline", "LiveStream", "Process", "LiveTcp" };

    /// <summary>Static tab keys in TabControl order: SystemInfo, StaticProcess, RecentFiles, ...</summary>
    public static readonly IReadOnlyList<string> StaticTabKeys = new[]
    {
        "SystemInfo", "StaticProcess", "RecentFiles", "Prefetch", "Amcache", "Autorun", "EventLog", "Network", "BrowsingHistory", "MFT"
    };

    private int _selectedDynamicTabIndex = -1;
    private int _selectedStaticTabIndex = 0;
    private bool _isDetachRestoreMode;
    private string _detachButtonToolTip = "Detach";

    /// <summary>Selected index of the dynamic analysis TabControl (Timeline/Live Stream/Live Process/Live TCP). -1 = none.</summary>
    public int SelectedDynamicTabIndex
    {
        get => _selectedDynamicTabIndex;
        set
        {
            if (SetProperty(ref _selectedDynamicTabIndex, value))
            {
                OnPropertyChanged(nameof(CurrentContentKey));
                OnPropertyChanged(nameof(ToolbarViewType));
            }
        }
    }

    /// <summary>Selected index of the static analysis TabControl (System Info/Process/Recent Files/...). -1 = none.</summary>
    public int SelectedStaticTabIndex
    {
        get => _selectedStaticTabIndex;
        set
        {
            if (SetProperty(ref _selectedStaticTabIndex, value))
            {
                OnPropertyChanged(nameof(CurrentContentKey));
                OnPropertyChanged(nameof(ToolbarViewType));
            }
        }
    }

    /// <summary>Current content key from selected tab (dynamic takes precedence over static). Used by MainWindow to set SharedContentArea and toolbar.</summary>
    public string? CurrentContentKey =>
        _selectedDynamicTabIndex >= 0 && _selectedDynamicTabIndex < DynamicTabKeys.Count
            ? DynamicTabKeys[_selectedDynamicTabIndex]
            : (_selectedStaticTabIndex >= 0 && _selectedStaticTabIndex < StaticTabKeys.Count ? StaticTabKeys[_selectedStaticTabIndex] : null);

    /// <summary>Toolbar mode derived from CurrentContentKey. Drives toolbar button visibility via ToolbarViewTypeToVisibilityConverter.</summary>
    public string ToolbarViewType => GetToolbarViewType(CurrentContentKey);

    /// <summary>True when current tab is detached and the Detach button should show Restore icon. Set by MainWindow via UpdateDetachState.</summary>
    public bool IsDetachRestoreMode
    {
        get => _isDetachRestoreMode;
        private set => SetProperty(ref _isDetachRestoreMode, value);
    }

    /// <summary>ToolTip for the Detach/Restore toolbar button. Set by MainWindow via UpdateDetachState.</summary>
    public string DetachButtonToolTip
    {
        get => _detachButtonToolTip;
        private set => SetProperty(ref _detachButtonToolTip, value ?? "Detach");
    }

    /// <summary>Called by MainWindow when tab selection or detach state changes. Drives Detach button icon and ToolTip.</summary>
    public void UpdateDetachState(bool isRestore)
    {
        IsDetachRestoreMode = isRestore;
        DetachButtonToolTip = isRestore ? "Restore" : "Detach";
    }

    private static string GetToolbarViewType(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "None";
        if (key == "StaticProcess") return "StaticProcess";
        if (key == "Network") return "Network";
        if (key == "Timeline" || key == "LiveStream" || key == "Process" || key == "LiveTcp") return key;
        return "Static";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
