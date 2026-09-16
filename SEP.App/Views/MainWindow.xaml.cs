using System;
using System.Windows;
using System.Windows.Controls;
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
            // WPF-UI hosts pages in a ScrollViewer that measures them with unlimited height, so a page's own
            // ScrollViewer never gets to scroll and long lists just run off the window with jumpy, invisible
            // scrolling. Give pages the real viewport instead; they scroll themselves (see Behaviors/SmoothScroll).
            if (RootNavigation.Template?.FindName("PART_NavigationViewContentPresenter", RootNavigation) is NavigationViewContentPresenter presenter)
            {
                void ConstrainPages()
                {
                    var host = FindDescendant<ScrollViewer>(presenter);
                    if (host == null) return;
                    host.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    host.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                }
                presenter.ApplyTemplate();
                ConstrainPages();
                presenter.Loaded += (_, _) => ConstrainPages();
            }

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

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deeper = FindDescendant<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
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
