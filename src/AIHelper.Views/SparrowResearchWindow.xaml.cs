using System.Windows;
using AIHelper.ViewModels;

namespace AIHelper.Views;
public partial class SparrowResearchWindow : Window
{
    private readonly SparrowResearchViewModel _viewModel;
    public SparrowResearchWindow(SparrowResearchViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => _viewModel.RefreshProviderHealth();
        Closed += (_, _) => _viewModel.Dispose();
    }
}
