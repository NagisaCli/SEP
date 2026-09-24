using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.ViewModels;

namespace SEP.App.Views.Pages;

public partial class LivePage : Page
{
    public LivePage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<LiveViewModel>();
    }
}
