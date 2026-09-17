using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Localization;
using SEP.App.Models;
using SEP.App.Resources;
using SEP.App.Services;

namespace SEP.App.ViewModels;

/// <summary>One bar of the category / status distribution charts.</summary>
public sealed record DistributionBar(string Label, int Count, double Fraction, string Color)
{
    public double Percent => Math.Round(Fraction * 100);
}

/// <summary>Dashboard numbers and lists; everything comes from the catalog and refreshes with it.</summary>
public partial class OverviewViewModel : ObservableObject
{
    private readonly IProjectCatalog _catalog;
    private readonly SessionService _sessions;
    private readonly E3dToolsService _tools;
    private readonly ToastService _toasts;

    public ProjectActions Actions { get; }

    [ObservableProperty] private int _projectCount;
    [ObservableProperty] private int _myCount;
    [ObservableProperty] private int _libraryCount;
    [ObservableProperty] private int _unreachableLibraries;
    [ObservableProperty] private int _categoryCount;
    [ObservableProperty] private int _onlineCount;
    [ObservableProperty] private int _lockedCount;
    [ObservableProperty] private string _activeLabel = string.Empty;
    [ObservableProperty] private string _activeMode = string.Empty;
    [ObservableProperty] private bool _e3dInstallOk;
    [ObservableProperty] private string _e3dInstallText = string.Empty;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _dataDir = string.Empty;
    [ObservableProperty] private bool _hasNotifications;

    public ObservableCollection<ProjectItem> QuickLaunch { get; } = new();
    public ObservableCollection<ProjectItem> Recent { get; } = new();
    public ObservableCollection<ProjectItem> Online { get; } = new();
    public ObservableCollection<DistributionBar> Categories { get; } = new();
    public ObservableCollection<DistributionBar> Statuses { get; } = new();
    public ObservableCollection<NotificationRecord> Notifications { get; } = new();

    public OverviewViewModel(IProjectCatalog catalog, SessionService sessions, E3dToolsService tools, ToastService toasts, ProjectActions actions, SepDataStore store)
    {
        _catalog = catalog;
        _sessions = sessions;
        _tools = tools;
        _toasts = toasts;
        Actions = actions;
        DataDir = store.DataDir;

        _catalog.Changed += (_, _) => OnUiThread(Refresh);
        _catalog.ScanStateChanged += (_, _) => OnUiThread(() => IsScanning = _catalog.IsScanning);
        Loc.Instance.LanguageChanged += (_, _) => OnUiThread(Refresh);
        Refresh();
        _ = ProbeAsync();
    }

    private static void OnUiThread(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) action(); else d.BeginInvoke(action);
    }

    public void Refresh()
    {
        var projects = _catalog.Projects;
        ProjectCount = projects.Count;
        MyCount = _catalog.MyProjects.Count;
        LibraryCount = _catalog.Libraries.Count;
        UnreachableLibraries = _catalog.Libraries.Count(l => !l.IsReachable);
        CategoryCount = _catalog.Categories.Count;
        OnlineCount = projects.Sum(p => p.OnlineCount);
        LockedCount = projects.Count(p => p.IsLocked);
        IsScanning = _catalog.IsScanning;

        var s = _catalog.Data.Settings;
        bool hasActive = !string.IsNullOrEmpty(s.LastLaunched);
        ActiveLabel = !hasActive ? Strings.Main_NoActiveProjectSet : (s.LastMode == "all" ? Strings.Launch_AllName : s.LastLaunched!);
        ActiveMode = !hasActive ? Strings.Overview_ActiveHint : s.LastMode switch
        {
            "all" => string.Format(Strings.My_CountFormat, _catalog.MyProjects.Count),
            "library" => Strings.Main_ModeLibrary,
            _ => Strings.Overview_ModeSingle,
        };

        string install = _tools.InstallDir;
        E3dInstallOk = File.Exists(_tools.EvarsBatPath);
        E3dInstallText = E3dInstallOk ? install : Strings.Diag_InstallDirMissing;

        Sync(QuickLaunch, _catalog.MyProjects.Take(8));
        Sync(Recent, projects.Where(p => p.DiscoveredAt != null).OrderByDescending(p => p.DiscoveredAt).ThenBy(p => p.Name).Take(6));
        Sync(Online, projects.Where(p => p.IsOnline).OrderByDescending(p => p.OnlineCount).Take(6));

        Categories.Clear();
        int total = Math.Max(1, projects.Count);
        foreach (var c in _catalog.Categories)
        {
            int n = projects.Count(p => p.Category?.Id == c.Id);
            if (n > 0) Categories.Add(new DistributionBar(c.Name, n, (double)n / total, c.Color));
        }
        int uncategorised = projects.Count(p => p.Category == null);
        if (uncategorised > 0) Categories.Add(new DistributionBar(Strings.Category_None, uncategorised, (double)uncategorised / total, "#6B7280"));

        Statuses.Clear();
        string[] colors = { "#34D399", "#60A5FA", "#FBBF24", "#A78BFA" };
        int i = 0;
        foreach (var token in IProjectCatalog.StatusOptions)
        {
            int n = projects.Count(p => p.Status == token);
            if (n > 0) Statuses.Add(new DistributionBar(StatusOption.LabelOf(token), n, (double)n / total, colors[i % colors.Length]));
            i++;
        }
        int noStatus = projects.Count(p => p.Status.Length == 0);
        if (noStatus > 0) Statuses.Add(new DistributionBar(Strings.Status_None, noStatus, (double)noStatus / total, "#6B7280"));

        Notifications.Clear();
        foreach (var n in _catalog.ActiveNotifications.OrderByDescending(n => n.CreatedAt)) Notifications.Add(n);
        HasNotifications = Notifications.Count > 0;
    }

    private static void Sync(ObservableCollection<ProjectItem> target, System.Collections.Generic.IEnumerable<ProjectItem> wanted)
    {
        var list = wanted.ToList();
        if (list.SequenceEqual(target)) return;
        target.Clear();
        foreach (var p in list) target.Add(p);
    }

    /// <summary>Session probe for the projects on the dashboard (my projects first, then the rest).</summary>
    [RelayCommand]
    private async Task ProbeAsync()
    {
        await _sessions.ProbeAsync(_catalog.MyProjects.Concat(_catalog.Projects).Distinct().ToList());
        OnUiThread(Refresh);
    }

    [RelayCommand]
    private void Dismiss(NotificationRecord? n)
    {
        if (n != null) _catalog.DismissNotification(n.Id);
    }

    [RelayCommand]
    private void DismissAll() => _catalog.DismissNotification("all");

    [RelayCommand]
    private void GoProjects() => Actions.Navigate(typeof(Views.Pages.ProjectsPage));

    [RelayCommand]
    private void GoMyProjects() => Actions.Navigate(typeof(Views.Pages.MyProjectsPage));

    [RelayCommand]
    private void GoTools() => Actions.Navigate(typeof(Views.Pages.ToolsPage));

    [RelayCommand]
    private void GoSettings() => Actions.Navigate(typeof(Views.Pages.SettingsPage));

    [RelayCommand]
    private void GoPlugins() => Actions.Navigate(typeof(Views.Pages.PluginsPage));
}
