using System.Windows;
using TokenViewer.App.ViewModels;

namespace TokenViewer.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Detach();
    }
}
