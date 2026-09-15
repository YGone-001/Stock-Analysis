using System.Windows;
using AIHelper.ViewModels;

namespace AIHelper.Views;
public partial class SparrowResearchWindow : Window
{
    public SparrowResearchWindow(SparrowResearchViewModel viewModel) { InitializeComponent(); DataContext = viewModel; }
}
