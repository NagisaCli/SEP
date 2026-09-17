using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.ViewModels;

namespace SEP.App.Views.Pages;

public partial class ToolsPage : Page
{
    public ToolsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<ToolsViewModel>();
    }
}
