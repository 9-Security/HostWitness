using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WinDFIR.Core.Index;
using WinDFIR.UI.ViewModels;

namespace WinDFIR.UI.Views;

public partial class LiveTcpView : UserControl
{
    private readonly LiveTcpViewModel _viewModel;

    public LiveTcpView(IActivityIndex index)
    {
        InitializeComponent();
        _viewModel = new LiveTcpViewModel(index);
        DataContext = _viewModel;
    }

    public void Refresh()
    {
        _viewModel.Refresh();
    }

    public void Clear()
    {
        _viewModel.Clear();
        FilterTextBox.Clear();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Refresh();
    }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _viewModel.FilterText = FilterTextBox.Text;
    }

    public async Task ToggleResolveAddressAsync(bool isEnabled)
    {
        _viewModel.ResolveAddress = isEnabled;
        if (isEnabled)
            await _viewModel.ResolveAddressesAsync();
        else
            _viewModel.ResetResolvedAddresses();
    }

    public void SetProtocolFilters(bool tcpV4, bool tcpV6, bool udpV4, bool udpV6)
    {
        _viewModel.ShowTcpV4 = tcpV4;
        _viewModel.ShowTcpV6 = tcpV6;
        _viewModel.ShowUdpV4 = udpV4;
        _viewModel.ShowUdpV6 = udpV6;
    }

    public IReadOnlyCollection<string> GetStateFilters()
    {
        return _viewModel.GetStateFilters();
    }

    public void SetStateFilters(IEnumerable<string> states)
    {
        _viewModel.SetStateFilters(states);
    }
}
