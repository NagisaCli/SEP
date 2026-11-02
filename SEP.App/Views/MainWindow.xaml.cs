using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.Behaviors;
using SEP.App.Controls;
using SEP.App.Services;
using SEP.App.ViewModels;
using SEP.App.Views.Pages;
using Wpf.Ui.Controls;

namespace SEP.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainWindowViewModel _viewModel;
    private readonly NavItem[] _navItems;
    private NavItem? _selectedNav;

    public MainWindow()
    {
        App.Log("MainWindow Constructor enter");
        InitializeComponent();
        App.Log("MainWindow InitializeComponent finished");

        _viewModel = App.Services.GetRequiredService<MainWindowViewModel>();
        DataContext = _viewModel;
        _navItems = new[] { NavOverview, NavProjects, NavMyProjects, NavPlugins, NavUsers, NavTools, NavSettings };

        App.Services.GetRequiredService<ToastService>().Attach(SnackbarHost);
        App.Services.GetRequiredService<ProjectActions>().NavigateRequested += (page, _) => Dispatcher.BeginInvoke(() => Navigate(page));
        ContentFrame.Navigated += OnNavigated;
        // Ctrl+K: project search from anywhere
        InputBindings.Add(new KeyBinding(new RelayCommand(OpenSearch), Key.K, ModifierKeys.Control));

        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        App.Log("MainWindow_Loaded enter");
        try
        {
            var page = StartPageFromArgs();
            App.Log($"Navigating to {page.Name}");
            bool ok = Navigate(page);
            App.Log($"Navigate result: {ok}");

            string? panelArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--plugin-panel=", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(panelArg))
            {
                string p = panelArg["--plugin-panel=".Length..].Trim().ToLowerInvariant();
                App.Services.GetRequiredService<PluginsViewModel>().Panel = p;
            }
        }
        catch (Exception ex)
        {
            App.Log($"Navigate exception: {ex}");
        }
    }

    /// <summary>
    /// Shows a page. Pages are DI singletons hosted in the canvas Frame; the Frame keeps no history (there is no back
    /// button), and the sidebar entry of the page on screen is checked once the navigation has happened.
    /// </summary>
    public bool Navigate(Type pageType)
    {
        if (App.Services.GetService(pageType) is not Page page) return false;
        if (ReferenceEquals(ContentFrame.Content, page)) return true;
        return ContentFrame.Navigate(page);
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        while (ContentFrame.CanGoBack) ContentFrame.RemoveBackEntry();
        if (e.Content is not Page page) return;

        var type = page.GetType();
        foreach (var item in _navItems) item.IsChecked = item.PageType == type;
        _selectedNav = _navItems.FirstOrDefault(i => i.IsChecked == true);
        CrumbText.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new Binding(nameof(Page.Title)) { Source = page });
        // after layout, so the row's position is final (the first navigation happens before the window is laid out)
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => MoveNavSelection(animate: true));

        // page transition: fade in and settle 8 px upward on a spring
        ContentFrame.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        FrameSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = SpringEase.Default });
    }

    /// <summary>
    /// Puts the sidebar's one selection highlight under the checked row. A page change slides it there (spring ease);
    /// the first placement and layout changes (window resize, the session card growing) snap it without motion.
    /// </summary>
    private void MoveNavSelection(bool animate)
    {
        var item = _selectedNav;
        if (item == null || !item.IsVisible || !NavHost.IsVisible)
        {
            NavSelection.Opacity = 0;
            return;
        }

        Point origin = item.TransformToAncestor(NavHost).Transform(new Point(0, 0));
        NavSelection.Width = item.ActualWidth;
        NavSelection.Height = item.ActualHeight;
        NavSelection.Margin = new Thickness(origin.X, 0, 0, 0);

        bool first = NavSelection.Opacity == 0;
        NavSelection.Opacity = 1;
        if (!animate || first)
        {
            NavSelectionShift.BeginAnimation(TranslateTransform.YProperty, null);
            NavSelectionShift.Y = origin.Y;
            return;
        }
        NavSelectionShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(origin.Y, TimeSpan.FromMilliseconds(340)) { EasingFunction = SpringEase.Default });
    }

    private void OnNavLayoutChanged(object sender, SizeChangedEventArgs e)
    {
        if (_selectedNav != null) MoveNavSelection(animate: false);
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is NavItem { PageType: { } type }) Navigate(type);
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => OpenSearch();

    private void OpenSearch()
    {
        Navigate(typeof(ProjectsPage));
        var projects = App.Services.GetRequiredService<ProjectsPage>();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, projects.FocusSearch);
    }

    // The title bar spans only the canvas column, so the product block is the sidebar's drag handle.
    private void OnBrandMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
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

    private void NavMyProjects_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(SEP.App.Behaviors.ProjectDragDrop.DragFormat) || e.Data.GetDataPresent("SEP_ProjectItem"))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void NavMyProjects_Drop(object sender, DragEventArgs e)
    {
        SEP.App.Models.ProjectItem? project = null;
        if (e.Data.GetData(SEP.App.Behaviors.ProjectDragDrop.DragFormat) is SEP.App.Behaviors.ProjectDragPayload payload)
        {
            project = payload.Project;
        }
        else if (e.Data.GetData("SEP_ProjectItem") is SEP.App.Models.ProjectItem pi)
        {
            project = pi;
        }

        if (project != null)
        {
            var catalog = App.Services.GetRequiredService<IProjectCatalog>();
            catalog.SetMyProjects(new[] { project }, true);
            App.Services.GetRequiredService<ToastService>().Success(string.Format(Localization.Loc.Instance["Batch_AddedToMine"], 1));
            e.Handled = true;
        }
    }
}
