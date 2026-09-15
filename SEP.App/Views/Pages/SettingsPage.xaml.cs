using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.ViewModels;

namespace SEP.App.Views.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<SettingsViewModel>();
    }
}
