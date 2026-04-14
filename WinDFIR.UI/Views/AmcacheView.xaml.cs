using System.Windows.Controls;
using WinDFIR.UI.ViewModels;

namespace WinDFIR.UI.Views;

public partial class AmcacheView : UserControl
{
    private readonly AmcacheViewModel _viewModel;

    public AmcacheView()
    {
        InitializeComponent();
        _viewModel = new AmcacheViewModel();
        DataContext = _viewModel;
        AmcacheDataGrid.ItemsSource = _viewModel.Entries;
    }

    public void Refresh()
    {
        _viewModel.Refresh();
    }
}
