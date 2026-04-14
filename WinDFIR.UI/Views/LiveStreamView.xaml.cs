using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;
using WinDFIR.UI.ViewModels;

namespace WinDFIR.UI.Views;

public partial class LiveStreamView : UserControl
{
    private readonly LiveStreamViewModel _viewModel;
    private readonly ObservableCollection<ActivityEvent> _streamEvents;

    public LiveStreamView(IActivityIndex index)
    {
        InitializeComponent();
        _viewModel = new LiveStreamViewModel(index);
        DataContext = _viewModel;
        
        _streamEvents = _viewModel.StreamEvents;
        StreamDataGrid.ItemsSource = _streamEvents;
        
        // Set initial state to stopped (paused)
        _viewModel.Pause();
        StatusTextBlock.Text = "Stopped";
        StatusTextBlock.Foreground = System.Windows.Media.Brushes.Gray;
    }

    public void AddEvent(ActivityEvent activityEvent)
    {
        _viewModel.AddEvent(activityEvent);
    }

    public void Pause()
    {
        _viewModel.Pause();
        UpdatePauseButton();
    }

    public void Resume()
    {
        _viewModel.Resume();
        UpdatePauseButton();
    }

    public bool IsPaused => _viewModel.IsPaused;

    private void UpdatePauseButton()
    {
        if (_viewModel.IsPaused)
        {
            StatusTextBlock.Text = "Paused";
            StatusTextBlock.Foreground = System.Windows.Media.Brushes.Orange;
        }
        else if (_streamEvents.Count == 0)
        {
            StatusTextBlock.Text = "Stopped";
            StatusTextBlock.Foreground = System.Windows.Media.Brushes.Gray;
        }
        else
        {
            StatusTextBlock.Text = "Streaming...";
            StatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
        }
    }

    public void Clear()
    {
        _viewModel.Clear();
    }

    private void StreamDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StreamDataGrid.SelectedItem is ActivityEvent selectedEvent)
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
