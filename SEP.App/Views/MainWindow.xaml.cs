using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.ViewModels;
using SEP.App.Views.Pages;
using Wpf.Ui.Controls;

namespace SEP.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        App.Log("MainWindow Constructor enter");
        InitializeComponent();
        App.Log("MainWindow InitializeComponent finished");

        _viewModel = App.Services.GetRequiredService<MainWindowViewModel>();
        DataContext = _viewModel;
        App.Log("MainWindow DataContext set");

        RootNavigation.SetServiceProvider(App.Services);
        App.Log("RootNavigation SetServiceProvider finished");

        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        App.Log("MainWindow_Loaded enter");
        try
        {
            App.Log("RootNavigation navigating to ProjectsPage");
            bool ok = RootNavigation.Navigate(typeof(ProjectsPage));
            App.Log($"RootNavigation.Navigate result: {ok}");
        }
        catch (Exception ex)
        {
            App.Log($"Navigate exception: {ex}");
        }
    }
}
