using System.Text;
using System.Windows;
using System.Windows.Controls;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.UI.ViewModels;

namespace WinDFIR.UI.Views;

public partial class BrowsingHistoryView : UserControl
{
    private readonly BrowsingHistoryViewModel _viewModel;

    public BrowsingHistoryView(IActivityIndex index)
    {
        InitializeComponent();
        _viewModel = new BrowsingHistoryViewModel(index);
        DataContext = _viewModel;
        
        HistoryDataGrid.ItemsSource = _viewModel.BrowserEvents;
    }

    public void Refresh()
    {
        _viewModel.Refresh();
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
        _viewModel.ClearFilters();
        StartDatePicker.SelectedDate = null;
        EndDatePicker.SelectedDate = null;
        FilterTextBox.Clear();
    }

    private void HistoryDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryDataGrid.SelectedItem is ActivityEvent selectedEvent)
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
            
            if (!string.IsNullOrEmpty(selectedEvent.ObjectUrl))
            {
                details.AppendLine($"URL: {selectedEvent.ObjectUrl}");
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
