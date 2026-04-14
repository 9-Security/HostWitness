using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WinDFIR.Core.Index;
using WinDFIR.UI.ViewModels;

namespace WinDFIR.UI.Views;

public partial class NetworkView : UserControl
{
    private readonly NetworkViewModel _viewModel;

    public NetworkView(IActivityIndex index)
    {
        InitializeComponent();
        _viewModel = new NetworkViewModel(index);
        DataContext = _viewModel;
    }

    public void Refresh()
    {
        var selectedKey = _viewModel.SelectedConnection;
        _viewModel.Refresh();
        if (selectedKey.HasValue)
        {
            var match = _viewModel.Connections.FirstOrDefault(item => item.NetworkFlowKey.Equals(selectedKey.Value));
            if (match != null)
                ConnectionsDataGrid.SelectedItem = match;
        }
        UpdateHighlighting();
    }

    private void UpdateHighlighting()
    {
        // Highlight new connections in the DataGrid
        foreach (var item in ConnectionsDataGrid.Items)
        {
            if (item is NetworkConnectionItem connectionItem)
            {
                var row = ConnectionsDataGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
                if (row != null)
                {
                    if (_viewModel.HighlightedConnections.Contains(connectionItem.NetworkFlowKey))
                    {
                        row.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightYellow);
                    }
                    else
                    {
                        row.Background = System.Windows.Media.Brushes.White;
                    }
                }
            }
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Refresh();
    }

    private void ConnectionsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConnectionsDataGrid.SelectedItem is NetworkConnectionItem item)
        {
            _viewModel.SelectedConnection = item.NetworkFlowKey;
        }
        else
        {
            _viewModel.SelectedConnection = null;
        }
    }
}
