using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.Services;
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

        RootNavigation.SetServiceProvider(App.Services);
        App.Services.GetRequiredService<ToastService>().Attach(SnackbarHost);
        App.Services.GetRequiredService<ProjectActions>().NavigateRequested += (page, _) => Dispatcher.BeginInvoke(() => RootNavigation.Navigate(page));

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
    /// Optional start page: <c>SEP.exe --page=projects</c> (overview | projects | mine | plugins | users | tools | settings),
    /// handy for shortcuts and for UI checks; anything else opens the overview.
    /// </summary>
    private static Type StartPageFromArgs()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (!arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase)) continue;
            switch (arg["--page=".Length..].Trim().ToLowerInvariant())
            {
                case "projects": return typeof(ProjectsPage);
                case "mine": case "my": return typeof(MyProjectsPage);
                case "plugins": return typeof(PluginsPage);
                case "users": return typeof(UserAdminPage);
                case "tools": case "health": return typeof(ToolsPage);
                case "settings": return typeof(SettingsPage);
            }
        }
        return typeof(OverviewPage);
    }
}
