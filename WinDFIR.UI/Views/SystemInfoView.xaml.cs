using System.Windows.Controls;
using WinDFIR.UI.ViewModels;

namespace WinDFIR.UI.Views;

public partial class SystemInfoView : UserControl
{
    private readonly SystemInfoViewModel _viewModel;

    public SystemInfoView()
    {
        InitializeComponent();
        _viewModel = new SystemInfoViewModel();
        DataContext = _viewModel;
        _viewModel.Refresh();
    }

    public void Refresh()
    {
        _viewModel.Refresh();
    }
}
