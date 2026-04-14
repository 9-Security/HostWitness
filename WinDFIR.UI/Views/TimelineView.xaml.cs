using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.Core.Settings;
using WinDFIR.UI.ViewModels;

namespace WinDFIR.UI.Views;

public partial class TimelineView : UserControl
{
    private readonly TimelineViewModel _viewModel;

    public TimelineView(IActivityIndex index)
    {
        InitializeComponent();
        _viewModel = new TimelineViewModel(index);
        DataContext = _viewModel;
        
        TimelineDataGrid.ItemsSource = _viewModel.Events;
    }

    public TimelineViewModel ViewModel => _viewModel;

    public void Refresh()
    {
        _viewModel.Refresh();
    }

    /// <summary>Switch timeline to a snapshot index (e.g. after Open Snapshot).</summary>
    public void SetSnapshotIndex(IActivityIndex snapshotIndex)
    {
        _viewModel.SetIndex(snapshotIndex);
    }

    /// <summary>Apply drill-down filter by process (from Live Process selection). Switches to events for this process only.</summary>
    public void ApplyProcessFilter(ProcessKey processKey)
    {
        _viewModel.ApplyProcessFilter(processKey);
    }

    public void Clear()
    {
        _viewModel.ClearView();
        _viewModel.ClearProcessFilter();
        StartDatePicker.SelectedDate = null;
        EndDatePicker.SelectedDate = null;
        FilterTextBox.Clear();
        DetailsTextBlock.Text = string.Empty;
    }

    private void ClearProcessFilterButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearProcessFilter();
    }

    private void StartDatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StartDatePicker.SelectedDate.HasValue)
        {
            _viewModel.StartTime = StartDatePicker.SelectedDate.Value.Date;
        }
        else
        {
            _viewModel.StartTime = null;
        }
    }

    private void EndDatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EndDatePicker.SelectedDate.HasValue)
        {
            _viewModel.EndTime = EndDatePicker.SelectedDate.Value.Date.AddDays(1).AddTicks(-1);
        }
        else
        {
            _viewModel.EndTime = null;
        }
    }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            _viewModel.FilterText = textBox.Text;
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        Clear();
    }

    private void TimeRangeToday_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetTimeRangeToday();
        StartDatePicker.SelectedDate = _viewModel.StartTime?.Date;
        EndDatePicker.SelectedDate = _viewModel.EndTime?.Date;
    }

    private void TimeRange24h_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetTimeRangeLast24Hours();
        StartDatePicker.SelectedDate = _viewModel.StartTime?.Date;
        EndDatePicker.SelectedDate = _viewModel.EndTime?.Date;
    }

    private void TimeRange7d_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetTimeRangeLast7Days();
        StartDatePicker.SelectedDate = _viewModel.StartTime?.Date;
        EndDatePicker.SelectedDate = _viewModel.EndTime?.Date;
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var n = _viewModel.Events.Count;
        if (n == 0)
        {
            MessageBox.Show("目前無事件可匯出（篩選結果為 0 筆）。請調整時間範圍或篩選條件。", "Timeline Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var settings = HostWitnessSettings.Load();
        var defaultDir = settings.Ui?.ExportDefaultDirectory?.Trim();
        var suggestedName = _viewModel.GetSuggestedExportFileName("csv");
        var dlg = new SaveFileDialog
        {
            Title = $"Export timeline events to CSV (將匯出 {n} 筆)",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = "csv",
            FileName = suggestedName,
            InitialDirectory = !string.IsNullOrEmpty(defaultDir) && Directory.Exists(defaultDir) ? defaultDir : null
        };
        if (dlg.ShowDialog() == true)
        {
            var err = _viewModel.ExportToCsv(dlg.FileName);
            if (err != null)
                MessageBox.Show($"匯出失敗：{err}\n\n請確認路徑可寫入、磁碟空間足夠。", "Timeline Export", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                MessageBox.Show($"已匯出 {n} 筆事件至：\n{dlg.FileName}", "Timeline Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        var n = _viewModel.Events.Count;
        if (n == 0)
        {
            MessageBox.Show("目前無事件可匯出（篩選結果為 0 筆）。請調整時間範圍或篩選條件。", "Timeline Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var settings = HostWitnessSettings.Load();
        var defaultDir = settings.Ui?.ExportDefaultDirectory?.Trim();
        var suggestedName = _viewModel.GetSuggestedExportFileName("json");
        var dlg = new SaveFileDialog
        {
            Title = $"Export timeline events to JSON (將匯出 {n} 筆)",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
            FileName = suggestedName,
            InitialDirectory = !string.IsNullOrEmpty(defaultDir) && Directory.Exists(defaultDir) ? defaultDir : null
        };
        if (dlg.ShowDialog() == true)
        {
            var err = _viewModel.ExportToJson(dlg.FileName);
            if (err != null)
                MessageBox.Show($"匯出失敗：{err}\n\n請確認路徑可寫入、磁碟空間足夠。", "Timeline Export", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                MessageBox.Show($"已匯出 {n} 筆事件至：\n{dlg.FileName}", "Timeline Export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void TimelineDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimelineDataGrid.SelectedItem is ActivityEvent selectedEvent)
        {
            var details = new StringBuilder();
            details.AppendLine($"Category: {selectedEvent.Category}");
            details.AppendLine($"Action: {selectedEvent.Action}");
            details.AppendLine($"Timestamp: {selectedEvent.Timestamp:O}");
            details.AppendLine($"Confidence: {selectedEvent.Confidence}");
            
            if (!string.IsNullOrEmpty(selectedEvent.Summary))
            {
                details.AppendLine($"Summary: {selectedEvent.Summary}");
            }
            
            details.AppendLine();
            
            if (selectedEvent.SubjectProcess.HasValue)
            {
                details.AppendLine($"Subject Process: {selectedEvent.SubjectProcess.Value}");
            }
            
            if (selectedEvent.SubjectUser.HasValue)
            {
                details.AppendLine($"Subject User: {selectedEvent.SubjectUser.Value}");
            }
            
            if (selectedEvent.ObjectFile.HasValue)
            {
                details.AppendLine($"Object File: {selectedEvent.ObjectFile.Value}");
            }
            
            if (!string.IsNullOrEmpty(selectedEvent.ObjectUrl))
            {
                details.AppendLine($"Object URL: {selectedEvent.ObjectUrl}");
            }
            
            details.AppendLine();
            details.AppendLine($"Evidence ({selectedEvent.Evidence.Count}):");
            foreach (var evidence in selectedEvent.Evidence)
            {
                details.AppendLine($"  - {evidence}");
            }
            
            details.AppendLine();
            details.AppendLine("Fields:");
            foreach (var field in selectedEvent.Fields)
            {
                details.AppendLine($"  {field.Key}: {field.Value}");
            }
            
            DetailsTextBlock.Text = details.ToString();
        }
        else
        {
            DetailsTextBlock.Text = string.Empty;
        }
    }
}
