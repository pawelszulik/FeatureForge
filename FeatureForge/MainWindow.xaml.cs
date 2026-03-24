using System.Windows;
using FeatureForge.ViewModels;

namespace FeatureForge;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
