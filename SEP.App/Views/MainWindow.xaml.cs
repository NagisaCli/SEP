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
            var page = StartPageFromArgs();
            App.Log($"RootNavigation navigating to {page.Name}");
            bool ok = RootNavigation.Navigate(page);
            App.Log($"RootNavigation.Navigate result: {ok}");
        }
        catch (Exception ex)
        {
            App.Log($"Navigate exception: {ex}");
        }
    }

    /// <summary>
    /// Optional start page: <c>SEP.exe --page=settings</c> (projects | users | health | settings),
    /// handy for shortcuts and for UI checks; anything else opens the project workbench.
    /// </summary>
    private static Type StartPageFromArgs()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (!arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase)) continue;
            switch (arg["--page=".Length..].Trim().ToLowerInvariant())
            {
                case "users": return typeof(UserAdminPage);
                case "health": return typeof(DiagnosticsPage);
                case "settings": return typeof(SettingsPage);
            }
        }
        return typeof(ProjectsPage);
    }
}
