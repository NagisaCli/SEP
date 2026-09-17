using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.ViewModels;

namespace SEP.App.Views.Pages;

public partial class MyProjectsPage : Page
{
    public MyProjectsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<MyProjectsViewModel>();
    }
}
