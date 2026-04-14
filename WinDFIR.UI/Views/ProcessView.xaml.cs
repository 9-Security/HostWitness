using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Providers;
using WinDFIR.UI.Services;
using WinDFIR.UI.ViewModels;

namespace WinDFIR.UI.Views;

public partial class ProcessView : UserControl
{
    private readonly ProcessViewModel _viewModel;
    private string _contextColumnHeader = string.Empty;
    private string _contextCellValue = string.Empty;
    private readonly List<string> _filterFields = new()
    {
        "ProcessName",
        "PID",
        "User",
        "CommandLine",
        "ParentPID",
        "Integrity",
        "ImagePath",
        "Company",
        "Hash",
        "OwnerSid",
        "SessionId",
        "ParentImagePath",
        "Authenticode",
        "Operation",
        "Path",
        "Result",
        "Duration"
    };
    private readonly List<string> _filterOperators = new()
    {
        "Contains",
        "Equals",
        "StartsWith",
        "EndsWith"
    };
    private readonly List<string> _filterActions = new()
    {
        "Include",
        "Exclude"
    };

    public ProcessView(IActivityIndex index)
    {
        InitializeComponent();
        _viewModel = new ProcessViewModel(index);
        DataContext = _viewModel;

        FilterFieldComboBox.ItemsSource = _filterFields;
        FilterOperatorComboBox.ItemsSource = _filterOperators;
        FilterActionComboBox.ItemsSource = _filterActions;
        FilterFieldComboBox.SelectedIndex = 0;
        FilterOperatorComboBox.SelectedIndex = 0;
        FilterActionComboBox.SelectedIndex = 0;
        FilterCombineComboBox.SelectedIndex = 0;

        UpdateCaptureToggleUi();
    }

    public void Refresh(bool force = false)
    {
        _viewModel.Refresh(force);
        UpdateProcessCount();
    }

    public void Clear()
    {
        _viewModel.ClearView();
        ProcessDataGrid.SelectedItem = null;
        UpdateProcessCount();
    }

    private void UpdateProcessCount()
    {
        var count = _viewModel.VisibleProcessCount;
        if (count > 0)
        {
            ProcessCountText.Text = $"共 {count} 個進程";
        }
        else
        {
            ProcessCountText.Text = "無數據 - 請先啟動數據收集（在 Timeline 或 Live Stream 分頁點擊 Start）";
        }
    }


    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        Refresh(true);
    }

    private void ProcessCaptureToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
        {
            _viewModel.IsCapturePaused = toggle.IsChecked == true;
            UpdateCaptureToggleUi();
        }
    }

    private void UpdateCaptureToggleUi()
    {
        ProcessCaptureToggleButton.IsChecked = _viewModel.IsCapturePaused;
        if (_viewModel.IsCapturePaused)
        {
            ProcessCaptureToggleButton.Content = "Capture: Paused";
            ProcessCaptureToggleButton.ToolTip = "Resume capture updates";
        }
        else
        {
            ProcessCaptureToggleButton.Content = "Capture: On";
            ProcessCaptureToggleButton.ToolTip = "Pause capture updates";
        }
    }

    private ProcessTreeItem? GetSelectedProcessTreeItem()
    {
        if (_viewModel.ShowProcessTree)
            return ProcessTreeView.SelectedItem as ProcessTreeItem;
        return ProcessDataGrid.SelectedItem as ProcessTreeItem;
    }

    private void ProcessFilterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedProcessTreeItem() is not ProcessTreeItem item)
            return;

        if (sender is not MenuItem menuItem || menuItem.Tag is not string tag)
            return;

        var parts = tag.Split('|');
        if (parts.Length != 2)
            return;

        var action = parts[0];
        var field = parts[1];
        var value = field switch
        {
            "ProcessName" => item.ProcessName,
            "PID" => item.ProcessKey.ProcessId.ToString(),
            "ImagePath" => item.ImagePath,
            "Company" => item.Company,
            "Hash" => item.Hash,
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(value))
            return;

        _viewModel.AddFilter(new ProcessFilterRule
        {
            Action = action,
            Field = field,
            Operator = "Contains",
            Value = value
        });

        Refresh(true);
    }

    private void ProcessDataGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _contextColumnHeader = string.Empty;
        _contextCellValue = string.Empty;

        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridCell)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (dep is DataGridCell cell)
        {
            _contextColumnHeader = cell.Column?.Header?.ToString() ?? string.Empty;
            _contextCellValue = GetCellDisplayText(cell);

            var row = FindParent<DataGridRow>(cell);
            if (row != null)
            {
                ProcessDataGrid.SelectedItem = row.Item;
                _viewModel.SelectedProcess = (row.Item as ProcessTreeItem)?.ProcessKey;
            }
        }
        else
        {
            // Right-click on row chrome / gap: still select that data row for dump / drill-down.
            var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row != null)
            {
                ProcessDataGrid.SelectedItem = row.Item;
                _viewModel.SelectedProcess = (row.Item as ProcessTreeItem)?.ProcessKey;
            }
        }
    }

    private void ProcessDataGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Context menu's OriginalSource is usually the DataGrid, not the cell — sync selection to the row under the cursor.
        if (sender is DataGrid grid)
        {
            var pos = System.Windows.Input.Mouse.GetPosition(grid);
            if (VisualTreeHelper.HitTest(grid, pos) is HitTestResult hit && hit.VisualHit is DependencyObject hitDep)
            {
                var row = FindParent<DataGridRow>(hitDep);
                if (row?.Item is ProcessTreeItem hitItem)
                {
                    grid.SelectedItem = hitItem;
                    _viewModel.SelectedProcess = hitItem.ProcessKey;
                }
            }
            // Fallback: if hit-test doesn't produce a row (row chrome/gap),
            // attempt to locate the row from OriginalSource.
            if (grid.SelectedItem is not ProcessTreeItem && e.OriginalSource is DependencyObject srcDep)
            {
                var row = FindParent<DataGridRow>(srcDep);
                if (row?.Item is ProcessTreeItem hitItem)
                {
                    grid.SelectedItem = hitItem;
                    _viewModel.SelectedProcess = hitItem.ProcessKey;
                }
            }
        }

        var hasValue = !string.IsNullOrWhiteSpace(_contextCellValue);
        CopyValueMenuItem.IsEnabled = hasValue;

        var isImagePath = string.Equals(_contextColumnHeader, "Image Path", StringComparison.OrdinalIgnoreCase);
        OpenDirectoryMenuItem.IsEnabled = isImagePath && hasValue;

        var hasSelectedRow = GetSelectedProcessTreeItem() is { ProcessKey.ProcessId: > 0 };
        CreateDumpMenuItem.IsEnabled = hasSelectedRow;
    }

    private void ProcessTreeView_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(ProcessTreeView);
        var dep = ProcessTreeView.InputHitTest(pos) as DependencyObject;
        var treeViewItem = dep != null ? FindParent<TreeViewItem>(dep) : null;
        if (treeViewItem?.DataContext is ProcessTreeItem item)
        {
            treeViewItem.IsSelected = true;
            _contextColumnHeader = "Image Path";
            _contextCellValue = item.ImagePath ?? string.Empty;
            _viewModel.SelectedProcess = item.ProcessKey;
        }
        else
        {
            _contextColumnHeader = string.Empty;
            _contextCellValue = string.Empty;
        }
    }

    private void ProcessTreeView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        ProcessTreeItem? item = ProcessTreeView.SelectedItem as ProcessTreeItem;
        var pos = System.Windows.Input.Mouse.GetPosition(ProcessTreeView);
        if (VisualTreeHelper.HitTest(ProcessTreeView, pos) is HitTestResult hit && hit.VisualHit is DependencyObject hitDep)
        {
            var tvi = FindParent<TreeViewItem>(hitDep);
            if (tvi?.DataContext is ProcessTreeItem hitItem)
            {
                tvi.IsSelected = true;
                item = hitItem;
                _viewModel.SelectedProcess = hitItem.ProcessKey;
            }
        }

        _contextCellValue = item?.ImagePath ?? string.Empty;
        _contextColumnHeader = "Image Path";

        var hasValue = !string.IsNullOrWhiteSpace(_contextCellValue);
        TreeCopyValueMenuItem.IsEnabled = hasValue;
        TreeOpenDirectoryMenuItem.IsEnabled = hasValue && !string.IsNullOrWhiteSpace(item?.ImagePath);
        TreeCreateDumpMenuItem.IsEnabled = item != null && item.ProcessKey.ProcessId > 0;
    }

    private void CopyValueMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var value = _contextCellValue;
        if (string.IsNullOrWhiteSpace(value) && GetSelectedProcessTreeItem() is ProcessTreeItem sel)
            value = sel.ProcessName ?? sel.ImagePath ?? sel.ProcessKey.ProcessId.ToString();
        if (!string.IsNullOrWhiteSpace(value))
            Clipboard.SetText(value);
    }

    private void OpenDirectoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var path = _contextCellValue;
        if (string.IsNullOrWhiteSpace(path) && GetSelectedProcessTreeItem() is ProcessTreeItem sel)
            path = sel.ImagePath;
        if (string.IsNullOrWhiteSpace(path))
            return;
        ShellLaunchHelper.TryRevealPathInExplorer(path, Window.GetWindow(this));
    }

    private void CreateMinidumpMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RunCreateDump(fullMemory: false);
    }

    private void CreateFullDumpMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RunCreateDump(fullMemory: true);
    }

    private void RunCreateDump(bool fullMemory)
    {
        if (GetSelectedProcessTreeItem() is not ProcessTreeItem item || item.ProcessKey.ProcessId == 0)
        {
            MessageBox.Show("Please select a process row first.", "Create Dump", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pid = (int)item.ProcessKey.ProcessId;
        var baseName = string.IsNullOrWhiteSpace(item.ProcessName) ? $"process_{pid}" : item.ProcessName.Replace(".", "_");
        var suffix = fullMemory ? "full" : "mini";
        var defaultName = $"{baseName}_{pid}_{suffix}.dmp";
        var dialog = new SaveFileDialog
        {
            Title = fullMemory ? "Save full process memory dump" : "Save minidump",
            Filter = "Dump (*.dmp)|*.dmp|All files (*.*)|*.*",
            DefaultExt = "dmp",
            FileName = defaultName
        };
        if (dialog.ShowDialog() != true)
            return;

        var (success, errorMessage) = ProcessMemoryDumper.DumpProcess(pid, dialog.FileName, fullMemory);
        if (success)
            MessageBox.Show($"Dump saved to:\n{dialog.FileName}", "Create Dump", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show($"Dump failed: {errorMessage}", "Create Dump", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        var current = child;
        while (current != null)
        {
            if (current is T parent)
                return parent;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static string GetCellDisplayText(DataGridCell cell)
    {
        if (cell.Content is TextBlock tb)
            return tb.Text ?? string.Empty;
        if (cell.Content is FrameworkElement root)
        {
            var nested = FindVisualChild<TextBlock>(root);
            if (nested != null)
                return nested.Text ?? string.Empty;
        }
        return string.Empty;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            var nested = FindVisualChild<T>(child);
            if (nested != null)
                return nested;
        }
        return null;
    }

    private void AddFilterButton_Click(object sender, RoutedEventArgs e)
    {
        var field = FilterFieldComboBox.SelectedItem as string ?? "ProcessName";
        var op = FilterOperatorComboBox.SelectedItem as string ?? "Contains";
        var action = FilterActionComboBox.SelectedItem as string ?? "Include";
        var value = FilterValueTextBox.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(value))
            return;

        _viewModel.AddFilter(new ProcessFilterRule
        {
            Field = field,
            Operator = op,
            Action = action,
            Value = value.Trim()
        });

        FilterValueTextBox.Clear();
        Refresh();
    }

    private void FilterCombineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterCombineComboBox.SelectedItem is ComboBoxItem item)
            _viewModel.FilterCombineMode = item.Content?.ToString() ?? "AND";
        else if (FilterCombineComboBox.SelectedItem is string mode)
            _viewModel.FilterCombineMode = mode;

        Refresh(true);
    }

    private void RemoveFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Filters.Count == 0)
            return;

        var last = _viewModel.Filters[_viewModel.Filters.Count - 1];
        _viewModel.RemoveFilter(last);
        Refresh();
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearFilters();
        Refresh();
    }

    private void ProcessDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = ProcessDataGrid.SelectedItem as ProcessTreeItem;
        _viewModel.SelectedProcess = item?.ProcessKey;
    }

    /// <summary>Invoked when user requests drill-down to related events (double-click or menu). MainWindow subscribes and switches to Timeline with process filter.</summary>
    public Action<ProcessKey>? DrillDownRequested { get; set; }

    private void RequestDrillDown()
    {
        if (!_viewModel.SelectedProcess.HasValue)
        {
            MessageBox.Show("Select a process row first.", "Drill Down", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DrillDownRequested?.Invoke(_viewModel.SelectedProcess.Value);
    }

    private void ProcessDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        RequestDrillDown();
    }

    private void ShowRelatedEventsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RequestDrillDown();
    }

    /// <summary>Returns the currently selected process key (for MainWindow Drill Down menu).</summary>
    public ProcessKey? GetSelectedProcessKey() => _viewModel.SelectedProcess;
}
