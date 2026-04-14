using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using WinDFIR.UI.ViewModels;

namespace WinDFIR.UI.Views;

public partial class LiveTcpStateFilterDialog : Window
{
    public ObservableCollection<StateOption> Options { get; } = new();

    public LiveTcpStateFilterDialog(IEnumerable<string>? selectedStates)
    {
        InitializeComponent();
        DataContext = this;

        var selected = selectedStates?.ToHashSet(System.StringComparer.OrdinalIgnoreCase)
                       ?? new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var selectAll = selected.Count == 0;

        foreach (var option in BuildOptions())
        {
            option.IsChecked = selectAll || selected.Contains(option.Value);
            Options.Add(option);
        }
    }

    public IReadOnlyList<string> GetSelectedStates()
    {
        return Options.Where(o => o.IsChecked).Select(o => o.Value).ToList();
    }

    private void AllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var option in Options)
            option.IsChecked = true;
    }

    private void NoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var option in Options)
            option.IsChecked = false;
    }

    private void InvertButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var option in Options)
            option.IsChecked = !option.IsChecked;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private static IEnumerable<StateOption> BuildOptions()
    {
        return new[]
        {
            new StateOption("Closed", "CLOSED"),
            new StateOption("Listen", "LISTEN"),
            new StateOption("Syn Sent", "SYN_SENT"),
            new StateOption("Syn Received", "SYN_RECEIVED"),
            new StateOption("Established", "ESTABLISHED"),
            new StateOption("Fin Wait 1", "FIN_WAIT_1"),
            new StateOption("Fin Wait 2", "FIN_WAIT_2"),
            new StateOption("Close Wait", "CLOSE_WAIT"),
            new StateOption("Closing", "CLOSING"),
            new StateOption("Ack", "ACK"),
            new StateOption("Time Wait", "TIME_WAIT"),
            new StateOption("Delete TCB", "DELETE_TCB")
        };
    }
}

public class StateOption : BaseViewModel
{
    public StateOption(string display, string value)
    {
        Display = display;
        Value = value;
    }

    public string Display { get; }
    public string Value { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}
