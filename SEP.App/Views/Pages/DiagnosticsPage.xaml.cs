using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.ViewModels;

namespace SEP.App.Views.Pages;

public partial class DiagnosticsPage : Page
{
    public DiagnosticsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<DiagnosticsViewModel>();
    }
}
