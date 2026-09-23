using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    [ObservableProperty]
    private int _hiddenCount;

    /// <summary>What to say under an empty project list: nothing found, or everything filtered out.</summary>
    [ObservableProperty]
    private string _emptyText = string.Empty;

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

/// <summary>A filter entry of the category / status / tag pickers ("" = any).</summary>
public sealed record FilterOption(string Value, string Label, string? Color = null)
{
    public override string ToString() => Label;
}

public partial class ProjectsViewModel : ObservableObject
{
    private readonly IProjectCatalog _catalog;
    private readonly IE3dLauncherService _launcherService;
    private readonly IE3dProjectService _projectService;
    private readonly SessionService _sessions;
    private readonly IDialogService _dialogs;
    private readonly ToastService _toasts;

    public ProjectActions Actions { get; }
    public ObservableCollection<LibraryGroup> Groups { get; } = new();
    public ObservableCollection<FilterOption> CategoryFilters { get; } = new();
    public ObservableCollection<FilterOption> StatusFilters { get; } = new();
    public ObservableCollection<FilterOption> TagFilters { get; } = new();

    [ObservableProperty]
    private string _filterTab = "All"; // "All", "Mine", "Local", "Unc"

    [ObservableProperty]
    private string _searchKeyword = string.Empty;

    [ObservableProperty]
    private FilterOption? _categoryFilter;

    [ObservableProperty]
    private FilterOption? _statusFilter;

    [ObservableProperty]
    private FilterOption? _tagFilter;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddLibraryCommand))]
    private string _newLibraryPath = string.Empty;

    [ObservableProperty]
    private bool _showAddLibrary;

    /// <summary>True while scanning or launching; launch/refresh buttons are disabled meanwhile.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadLibraryCommand))]
    [NotifyCanExecuteChangedFor(nameof(RescanAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(RescanLibraryCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddLibraryCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private string _notificationText = string.Empty;

    [ObservableProperty]
    private bool _hasLibraries;

    [ObservableProperty]
    private int _visibleCount;

    // batch mode
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchBarVisible))]
    private bool _batchMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchBarVisible))]
    [NotifyPropertyChangedFor(nameof(SelectedCountText))]
    private int _selectedCount;

    public bool BatchBarVisible => BatchMode;
    public string SelectedCountText => string.Format(Strings.Batch_Selected, SelectedCount);

    // projects whose IsSelected we already watch (instances survive rebuilds, see ProjectCatalog.RebuildModels)
    private readonly HashSet<ProjectItem> _watched = new();

    public ProjectsViewModel(IProjectCatalog catalog, IE3dLauncherService launcherService, IE3dProjectService projectService,
        SessionService sessions, IDialogService dialogs, ToastService toasts, ProjectActions actions)
    {
        _catalog = catalog;
        _launcherService = launcherService;
        _projectService = projectService;
        _sessions = sessions;
        _dialogs = dialogs;
        _toasts = toasts;
        Actions = actions;

        _catalog.Changed += (_, _) => OnUiThread(Rebuild);
        _catalog.ScanStateChanged += (_, _) => OnUiThread(() => IsLoading = _catalog.IsScanning);
        Loc.Instance.LanguageChanged += (_, _) => OnUiThread(() => { foreach (var g in Groups) g.RefreshStatus(); RebuildFilters(); ApplyFilter(); UpdateSummary(); });

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
        foreach (var p in _catalog.Projects)
        {
            if (_watched.Add(p)) p.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProjectItem.IsSelected)) OnSelectionChanged(); };
        }
        RebuildFilters();
        ApplyFilter();
        UpdateSummary();
    }

    // true while the picker lists are being refilled, so re-selecting the same value does not re-filter three times
    private bool _refilling;

    private void RebuildFilters()
    {
        _refilling = true;
        try
        {
            CategoryFilter = Refill(CategoryFilters, new[] { new FilterOption("", Strings.Filter_AnyCategory) }
                .Concat(_catalog.Categories.Select(c => new FilterOption(c.Id, c.Name, c.Color))), CategoryFilter);
            StatusFilter = Refill(StatusFilters, new[] { new FilterOption("", Strings.Filter_AnyStatus) }
                .Concat(IProjectCatalog.StatusOptions.Select(t => new FilterOption(t, StatusOption.LabelOf(t)))), StatusFilter);
            TagFilter = Refill(TagFilters, new[] { new FilterOption("", Strings.Filter_AnyTag) }
                .Concat(_catalog.AllTags.Select(t => new FilterOption(t, t))), TagFilter);
        }
        finally { _refilling = false; }
    }

    private static FilterOption Refill(ObservableCollection<FilterOption> target, IEnumerable<FilterOption> items, FilterOption? selected)
    {
        string current = selected?.Value ?? string.Empty;
        target.Clear();
        foreach (var i in items) target.Add(i);
        return target.FirstOrDefault(o => o.Value == current) ?? target[0];
    }

    partial void OnSearchKeywordChanged(string value) => ApplyFilter();
    partial void OnFilterTabChanged(string value) => ApplyFilter();
    partial void OnCategoryFilterChanged(FilterOption? value) { if (!_refilling) ApplyFilter(); }
    partial void OnStatusFilterChanged(FilterOption? value) { if (!_refilling) ApplyFilter(); }
    partial void OnTagFilterChanged(FilterOption? value) { if (!_refilling) ApplyFilter(); }

    public void ApplyFilter()
    {
        string kw = SearchKeyword.Trim();
        string cat = CategoryFilter?.Value ?? string.Empty;
        string st = StatusFilter?.Value ?? string.Empty;
        string tag = TagFilter?.Value ?? string.Empty;
        int visible = 0;
        foreach (var group in Groups)
        {
            var all = _catalog.Projects.Where(p => p.LibraryId == group.Library.Id).ToList();
            IEnumerable<ProjectItem> q = all;
            if (FilterTab == "Mine") q = q.Where(p => p.IsFavorite);
            else if (FilterTab == "Local") q = q.Where(p => !p.IsUnc);
            else if (FilterTab == "Unc") q = q.Where(p => p.IsUnc);
            if (cat.Length > 0) q = q.Where(p => p.Category?.Id == cat);
            if (st.Length > 0) q = q.Where(p => p.Status == st);
            if (tag.Length > 0) q = q.Where(p => p.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
            if (kw.Length > 0) q = q.Where(p => p.Matches(kw));

            var wanted = q.OrderByDescending(p => p.IsFavorite).ThenByDescending(p => p.IsActive)
                          .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (!wanted.SequenceEqual(group.Projects))
            {
                group.Projects.Clear();
                foreach (var p in wanted) group.Projects.Add(p);
            }
            group.HiddenCount = all.Count - wanted.Count;
            group.EmptyText = all.Count == 0 ? Strings.Lib_NoProjects : string.Format(Strings.Lib_NoVisibleProjects, group.HiddenCount);
            visible += wanted.Count;
        }
        VisibleCount = visible;
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
        _ = _sessions.ProbeAsync(_catalog.Projects.ToList());
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RescanLibraryAsync(LibraryGroup? group)
    {
        if (group == null) return;
        await _catalog.RescanLibraryAsync(group.Library.Id);
        OnUiThread(UpdateSummary);
    }

    [RelayCommand]
    private void ToggleAddLibrary() => ShowAddLibrary = !ShowAddLibrary;

    [RelayCommand]
    private void BrowseLibrary()
    {
        string? folder = _dialogs.PickFolder(Strings.Lib_BrowseTitle, _catalog.LocalProjectsDir);
        if (folder != null) NewLibraryPath = folder;
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
            _toasts.Result(ok, msg);
            if (ok) { NewLibraryPath = string.Empty; ShowAddLibrary = false; }
        }
        finally
        {
            IsLoading = _catalog.IsScanning;
        }
    }

    [RelayCommand]
    private async Task RemoveLibraryAsync(LibraryGroup? group)
    {
        if (group == null) return;
        bool ok = await _dialogs.ConfirmAsync(Strings.Lib_RemoveTitle, string.Format(Strings.Lib_RemoveBody, group.Library.Name), Strings.Lib_RemoveConfirm, danger: true);
        if (!ok) return;
        var (_, msg) = _catalog.RemoveLibrary(group.Library.Id);
        NotificationText = msg;
        _toasts.Info(msg);
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

    [RelayCommand]
    private void ExpandAll(bool expand)
    {
        foreach (var g in Groups) g.Library.IsExpanded = expand;
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
            _toasts.Result(res.Success, res.Message);
        }
        finally
        {
            IsLoading = _catalog.IsScanning;
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchKeyword = string.Empty;
        FilterTab = "All";
        CategoryFilter = CategoryFilters.FirstOrDefault();
        StatusFilter = StatusFilters.FirstOrDefault();
        TagFilter = TagFilters.FirstOrDefault();
    }

    // ── batch mode ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleBatchMode()
    {
        BatchMode = !BatchMode;
        if (!BatchMode) ClearSelection();
    }

    public void OnSelectionChanged() => SelectedCount = _catalog.Projects.Count(p => p.IsSelected);

    private IReadOnlyList<ProjectItem> Selected() => _catalog.Projects.Where(p => p.IsSelected).ToList();

    private void ClearSelection()
    {
        foreach (var p in _catalog.Projects) p.IsSelected = false;
        SelectedCount = 0;
    }

    [RelayCommand]
    private void SelectVisible(bool select)
    {
        foreach (var g in Groups) foreach (var p in g.Projects) p.IsSelected = select;
        OnSelectionChanged();
    }

    [RelayCommand]
    private async Task BatchLaunchAsync()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        IsLoading = true;
        try
        {
            NotificationText = Strings.Launch_AllStarting;
            _toasts.Info(Strings.Launch_AllStarting);
            var res = await _launcherService.SwitchAndLaunchMultipleAsync(sel);
            NotificationText = res.Message;
            _toasts.Result(res.Success, res.Message);
            if (res.Success)
            {
                BatchMode = false;
                ClearSelection();
            }
        }
        finally
        {
            IsLoading = _catalog.IsScanning;
        }
    }

    [RelayCommand]
    private void BatchAddToMine()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        _catalog.SetMyProjects(sel, true);
        _toasts.Success(string.Format(Strings.Batch_AddedToMine, sel.Count));
        ClearSelection();
    }

    [RelayCommand]
    private void BatchRemoveFromMine()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        _catalog.SetMyProjects(sel, false);
        _toasts.Success(string.Format(Strings.Batch_RemovedFromMine, sel.Count));
        ClearSelection();
    }

    [RelayCommand]
    private async Task BatchEditAsync()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (await _dialogs.EditProjectsAsync(sel))
        {
            _toasts.Success(string.Format(Strings.Batch_Edited, sel.Count));
            ClearSelection();
        }
    }

    [RelayCommand]
    private void CancelBatch()
    {
        BatchMode = false;
        ClearSelection();
    }
}
