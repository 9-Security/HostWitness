using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using WinDFIR.Providers;
using WinDFIR.UI.ViewModels;

namespace WinDFIR.UI.Views;

public partial class StaticProcessView : UserControl
{
    private readonly StaticProcessViewModel _viewModel;
    private int _contextPid;

    public StaticProcessView()
    {
        InitializeComponent();
        _viewModel = new StaticProcessViewModel();
        DataContext = _viewModel;
    }

    public void Refresh(bool force = false)
    {
        _ = _viewModel.RefreshAsync(force);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        Refresh(true);
    }

    private void ProcessDataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _contextPid = 0;

        if (e.OriginalSource is not DependencyObject dep)
            return;

        while (dep != null && dep is not DataGridCell)
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is not DataGridCell cell)
            return;

        var header = cell.Column?.Header?.ToString();
        if (!string.Equals(header, "PID", StringComparison.OrdinalIgnoreCase))
            return;

        var row = FindParent<DataGridRow>(cell);
        if (row?.Item is StaticProcessItem item)
            _contextPid = item.Id;

        // Keep SelectedProcess aligned with what the user right-clicked.
        if (row?.Item is StaticProcessItem selected)
            _viewModel.SelectedProcess = selected;
    }

    private void ProcessDataGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var isEnabled = _contextPid > 0;
        CreateMinidumpMenuItem.IsEnabled = isEnabled;
        CreateMinidumpMenuItem.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
        CreateFullDumpMenuItem.IsEnabled = isEnabled;
        CreateFullDumpMenuItem.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CreateMinidumpMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var pid = _contextPid > 0 ? _contextPid : _viewModel.SelectedProcess?.Id ?? 0;
        if (pid <= 0)
        {
            MessageBox.Show("Please right-click the PID cell to select a process first.", "Create minidump",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var defaultName = $"process_{pid}_mini.dmp";
        var dialog = new SaveFileDialog
        {
            Title = "Save minidump",
            Filter = "Dump (*.dmp)|*.dmp|All files (*.*)|*.*",
            DefaultExt = "dmp",
            FileName = defaultName
        };

        if (dialog.ShowDialog() != true)
            return;

        var (success, errorMessage) = ProcessMemoryDumper.DumpProcess(pid, dialog.FileName, fullMemory: false);
        if (success)
            MessageBox.Show($"Dump saved to:\n{dialog.FileName}", "Create minidump",
                MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show($"Dump failed: {errorMessage}", "Create minidump",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CreateFullDumpMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var pid = _contextPid > 0 ? _contextPid : _viewModel.SelectedProcess?.Id ?? 0;
        if (pid <= 0)
        {
            MessageBox.Show("Please right-click the PID cell to select a process first.", "Create full dump",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var defaultName = $"process_{pid}_full.dmp";
        var dialog = new SaveFileDialog
        {
            Title = "Save full dump",
            Filter = "Dump (*.dmp)|*.dmp|All files (*.*)|*.*",
            DefaultExt = "dmp",
            FileName = defaultName
        };

        if (dialog.ShowDialog() != true)
            return;

        var (success, errorMessage) = ProcessMemoryDumper.DumpProcess(pid, dialog.FileName, fullMemory: true);
        if (success)
            MessageBox.Show($"Dump saved to:\n{dialog.FileName}", "Create full dump",
                MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show($"Dump failed: {errorMessage}", "Create full dump",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
}
