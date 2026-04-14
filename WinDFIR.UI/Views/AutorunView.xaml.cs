using System.Text;
using System.Windows;
using System.Windows.Controls;
using WinDFIR.Core.Index;
using WinDFIR.UI.ViewModels;

namespace WinDFIR.UI.Views;

public partial class AutorunView : UserControl
{
    private readonly AutorunViewModel _viewModel;

    public AutorunView(IActivityIndex index)
    {
        InitializeComponent();
        _viewModel = new AutorunViewModel(index);
        DataContext = _viewModel;
        AutorunDataGrid.ItemsSource = _viewModel.Entries;
        LocationFilterComboBox.ItemsSource = _viewModel.LocationFilterOptions;
        LocationFilterComboBox.SelectedIndex = 0;
    }

    public void Refresh()
    {
        _viewModel.Refresh();
    }

    private void LocationFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null || LocationFilterComboBox.SelectedItem is not string location)
            return;
        _viewModel.LocationFilter = location;
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
            _viewModel.SearchText = tb.Text;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearFilters();
        LocationFilterComboBox.SelectedIndex = 0;
        SearchTextBox.Clear();
    }

    private void AutorunDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AutorunDataGrid.SelectedItem is not AutorunEntry entry)
        {
            DetailsTextBlock.Text = string.Empty;
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"Location: {entry.Location}");
        sb.AppendLine($"Entry: {entry.Entry}");
        sb.AppendLine($"Command: {entry.Command}");
        sb.AppendLine($"Last Write: {entry.LastWriteTime:O}");
        sb.AppendLine($"Hive: {entry.Hive}");
        sb.AppendLine($"Key Path: {entry.KeyPath}");
        sb.AppendLine($"Full Key: {entry.FullKeyPath}");
        sb.AppendLine($"Value Type: {entry.ValueType}");
        DetailsTextBlock.Text = sb.ToString();
    }
}
