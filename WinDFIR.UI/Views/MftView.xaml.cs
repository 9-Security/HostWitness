using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinDFIR.UI.ViewModels;

namespace WinDFIR.UI.Views;

public partial class MftView : UserControl
{
    private readonly MftViewModel _viewModel;

    public MftView()
    {
        InitializeComponent();
        _viewModel = new MftViewModel();
        DataContext = _viewModel;
    }

    private async void ChooseFile_Click(object sender, RoutedEventArgs e) => await _viewModel.ChooseFileAndLoadAsync();

    private async void LoadFromVolume_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new LoadMftFromRawDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true)
            return;

        await _viewModel.LoadFromVolumesAsync(dlg.DriveLetters);
    }

    public void Refresh()
    {
    }

    private void FirstPage_Click(object sender, RoutedEventArgs e) => GetTabContext(sender)?.GoToPage(1);

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabContext(sender);
        if (tab == null)
            return;

        tab.GoToPage(Math.Max(1, tab.CurrentPage - 1));
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabContext(sender);
        if (tab == null)
            return;

        tab.GoToPage(Math.Min(tab.TotalPages, tab.CurrentPage + 1));
    }

    private void LastPage_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabContext(sender);
        if (tab == null)
            return;

        tab.GoToPage(tab.TotalPages);
    }

    private void GoToPage_Click(object sender, RoutedEventArgs e)
    {
        var tab = GetTabContext(sender);
        if (tab == null)
            return;

        if (!tab.GoToPageFromInput())
            MessageBox.Show("Enter a valid page number.", "MFT", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTab == null)
        {
            MessageBox.Show("Load an MFT table first.", "MFT Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Export MFT entries to CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = "csv",
            FileName = $"mft_export_{SanitizeFileName(_viewModel.SelectedTab.Header)}.csv"
        };
        if (dlg.ShowDialog() == true)
        {
            var err = _viewModel.ExportSelectedTabToCsv(dlg.FileName);
            if (err != null)
                MessageBox.Show($"Export failed: {err}", "MFT Export", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                MessageBox.Show($"Exported {_viewModel.SelectedTab.FilteredEntries.Count} entries to {dlg.FileName}", "MFT Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTab == null)
        {
            MessageBox.Show("Load an MFT table first.", "MFT Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Export MFT entries to JSON",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
            FileName = $"mft_export_{SanitizeFileName(_viewModel.SelectedTab.Header)}.json"
        };
        if (dlg.ShowDialog() == true)
        {
            var err = _viewModel.ExportSelectedTabToJson(dlg.FileName);
            if (err != null)
                MessageBox.Show($"Export failed: {err}", "MFT Export", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                MessageBox.Show($"Exported {_viewModel.SelectedTab.FilteredEntries.Count} entries to {dlg.FileName}", "MFT Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private MftTabViewModel? GetTabContext(object sender)
    {
        return (sender as FrameworkElement)?.DataContext as MftTabViewModel ?? _viewModel.SelectedTab;
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = System.IO.Path.GetInvalidFileNameChars();
        var sanitized = value;
        foreach (var invalidChar in invalidChars)
            sanitized = sanitized.Replace(invalidChar, '_');
        return string.IsNullOrWhiteSpace(sanitized) ? "mft" : sanitized;
    }
}