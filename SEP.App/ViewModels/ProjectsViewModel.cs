using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

/// <summary>One library with the projects that pass the current filter.</summary>
public partial class LibraryGroup : ObservableObject
{
    public LibraryItem Library { get; }
    public ObservableCollection<ProjectItem> Projects { get; } = new();

    [ObservableProperty]
    private string _statusText = string.Empty;

    public LibraryGroup(LibraryItem library)
    {
        Library = library;
        library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LibraryItem.IsScanning) or nameof(LibraryItem.LastError)
                or nameof(LibraryItem.LastScan) or nameof(LibraryItem.ProjectCount))
                RefreshStatus();
        };
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        string count = string.Format(Strings.Lib_ProjectsCount, Library.ProjectCount);
        if (Library.IsScanning) StatusText = $"{count} · {Strings.Lib_Scanning}";
        else if (Library.LastError != null) StatusText = $"{count} · {Strings.Lib_CachedResults} · {Library.LastError}";
        else if (Library.LastScan != null) StatusText = $"{count} · {string.Format(Strings.Lib_ScannedAt, Library.LastScan.Value.ToString("g"))}";
        else StatusText = count;
    }
}

public partial class ProjectsViewModel : ObservableObject
{
    private readonly IProjectCatalog _catalog;
    private readonly IE3dLauncherService _launcherService;
    private readonly IE3dProjectService _projectService;
    private readonly MainWindowViewModel _mainVm;

    public ObservableCollection<LibraryGroup> Groups { get; } = new();

    [ObservableProperty]
    private string _filterTab = "All"; // "All", "Mine", "Local", "Unc"

    [ObservableProperty]
    private string _searchKeyword = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddLibraryCommand))]
    private string _newLibraryPath = string.Empty;

    /// <summary>True while scanning or launching; launch/refresh buttons are disabled meanwhile.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetActiveAndLaunchCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadLibraryCommand))]
    [NotifyCanExecuteChangedFor(nameof(RescanAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RescanLibraryCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddLibraryCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private string _notificationText = string.Empty;

    [ObservableProperty]
    private bool _hasLibraries;

    public ProjectsViewModel(IProjectCatalog catalog, IE3dLauncherService launcherService, IE3dProjectService projectService, MainWindowViewModel mainVm)
    {
        _catalog = catalog;
        _launcherService = launcherService;
        _projectService = projectService;
        _mainVm = mainVm;

        _catalog.Changed += (_, _) => OnUiThread(Rebuild);
        _catalog.ScanStateChanged += (_, _) => OnUiThread(() => IsLoading = _catalog.IsScanning);
        Loc.Instance.LanguageChanged += (_, _) => { foreach (var g in Groups) g.RefreshStatus(); };

        Rebuild();                                  // cached results are visible immediately…
        _ = RescanAllAsync(force: false);           // …and refreshed in the background
    }

    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private bool NotBusy() => !IsLoading;
    private bool CanAddLibrary() => !IsLoading && !string.IsNullOrWhiteSpace(NewLibraryPath);

    /// <summary>Rebuilds the groups from the catalog and applies the filter.</summary>
    public void Rebuild()
    {
        var libs = _catalog.Libraries;
        var byId = Groups.ToDictionary(g => g.Library.Id, g => g);

        Groups.Clear();
        foreach (var lib in libs)
        {
            var group = byId.TryGetValue(lib.Id, out var old) && ReferenceEquals(old.Library, lib) ? old : new LibraryGroup(lib);
            Groups.Add(group);
        }
        HasLibraries = Groups.Count > 0;
        ApplyFilter();
        UpdateSummary();
    }

    partial void OnSearchKeywordChanged(string value) => ApplyFilter();
    partial void OnFilterTabChanged(string value) => ApplyFilter();

    public void ApplyFilter()
    {
        string kw = SearchKeyword.Trim();
        foreach (var group in Groups)
        {
            IEnumerable<ProjectItem> q = _catalog.Projects.Where(p => p.LibraryId == group.Library.Id);
            if (FilterTab == "Mine") q = q.Where(p => p.IsFavorite);
            else if (FilterTab == "Local") q = q.Where(p => !p.IsUnc);
            else if (FilterTab == "Unc") q = q.Where(p => p.IsUnc);

            if (kw.Length > 0)
            {
                q = q.Where(p => p.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                                 (p.Code?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                 (p.DisplayName?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                 p.BatPath.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                                 p.Tags.Any(t => t.Contains(kw, StringComparison.OrdinalIgnoreCase)));
            }

            var wanted = q.OrderByDescending(p => p.IsFavorite).ThenByDescending(p => p.IsActive)
                          .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (!wanted.SequenceEqual(group.Projects))
            {
                group.Projects.Clear();
                foreach (var p in wanted) group.Projects.Add(p);
            }
        }
    }

    private void UpdateSummary()
    {
        int unreachable = _catalog.Libraries.Count(l => !l.IsReachable);
        NotificationText = unreachable > 0
            ? string.Format(Strings.Projects_ScanDone, _catalog.Projects.Count, unreachable)
            : string.Format(Strings.Projects_Loaded, _catalog.Projects.Count, _catalog.Libraries.Count);
    }

    // ── commands ─────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task RescanAllAsync() => RescanAllAsync(force: true);

    private async Task RescanAllAsync(bool force)
    {
        try
        {
            NotificationText = Strings.Projects_Scanning;
            await _catalog.RescanAllAsync(force);
        }
        catch (Exception ex)
        {
            NotificationText = string.Format(Strings.Projects_ScanError, ex.Message);
            return;
        }
        OnUiThread(UpdateSummary);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RescanLibraryAsync(LibraryGroup? group)
    {
        if (group == null) return;
        await _catalog.RescanLibraryAsync(group.Library.Id);
        OnUiThread(UpdateSummary);
    }

    [RelayCommand(CanExecute = nameof(CanAddLibrary))]
    private async Task AddLibraryAsync()
    {
        string path = NewLibraryPath.Trim();
        IsLoading = true;
        try
        {
            var (ok, msg) = await _catalog.AddLibraryAsync(path);
            NotificationText = msg;
            if (ok) NewLibraryPath = string.Empty;
        }
        finally
        {
            IsLoading = _catalog.IsScanning;
        }
    }

    [RelayCommand]
    private void RemoveLibrary(LibraryGroup? group)
    {
        if (group == null) return;
        var (_, msg) = _catalog.RemoveLibrary(group.Library.Id);
        NotificationText = msg;
    }

    [RelayCommand]
    private void OpenLibraryFolder(LibraryGroup? group)
    {
        if (group != null) _projectService.OpenFolder(group.Library.Path);
    }

    [RelayCommand]
    private void ToggleExpanded(LibraryGroup? group)
    {
        if (group != null) group.Library.IsExpanded = !group.Library.IsExpanded;
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task LoadLibraryAsync(LibraryGroup? group)
    {
        if (group == null) return;
        IsLoading = true;
        try
        {
            NotificationText = string.Format(Strings.Projects_SwitchingTo, group.Library.Name);
            var res = await _launcherService.LoadLibraryAndLaunchAsync(group.Library);
            NotificationText = res.Message;
            _mainVm.RefreshActiveProject();
        }
        finally
        {
            IsLoading = _catalog.IsScanning;
        }
    }

    [RelayCommand]
    private void ToggleFavorite(ProjectItem? item)
    {
        if (item == null) return;
        _catalog.ToggleMyProject(item);
        if (FilterTab == "Mine") ApplyFilter();   // the card leaves the list; otherwise the star just flips
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task SetActiveAndLaunchAsync(ProjectItem? item)
    {
        if (item == null) return;
        IsLoading = true;
        try
        {
            NotificationText = string.Format(Strings.Projects_SwitchingTo, item.Name);
            var res = await _launcherService.SwitchAndLaunchAsync(item);
            NotificationText = res.Message;
            _mainVm.RefreshActiveProject();
        }
        finally
        {
            IsLoading = _catalog.IsScanning;
        }
    }

    [RelayCommand]
    private void OpenProjectFolder(ProjectItem? item)
    {
        if (item == null) return;
        _projectService.OpenFolder(item.ProjectDir);
    }
}
