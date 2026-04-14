using System.Windows.Controls;
using WinDFIR.UI.ViewModels;

namespace WinDFIR.UI.Views;

public partial class PrefetchView : UserControl
{
    private readonly PrefetchViewModel _viewModel;

    public PrefetchView()
    {
        InitializeComponent();
        _viewModel = new PrefetchViewModel();
        DataContext = _viewModel;
        PrefetchDataGrid.ItemsSource = _viewModel.PrefetchItems;
        ReferencedFilesDataGrid.ItemsSource = _viewModel.ReferencedFiles;
    }

    public void Refresh()
    {
        _viewModel.Refresh();
    }
}
