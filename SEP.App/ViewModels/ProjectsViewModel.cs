using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Models;
using SEP.App.Resources;
using SEP.App.Services;

namespace SEP.App.ViewModels;

public partial class ProjectsViewModel : ObservableObject
{
    private readonly IE3dProjectService _projectService;
    private readonly IE3dLauncherService _launcherService;
    private readonly MainWindowViewModel _mainVm;

    [ObservableProperty]
    private ObservableCollection<ProjectItem> _projects = new();

    [ObservableProperty]
    private ObservableCollection<ProjectItem> _filteredProjects = new();

    [ObservableProperty]
    private ProjectItem? _selectedProject;

    [ObservableProperty]
    private string _filterTab = "All"; // "All", "Favorites", "Local", "Unc"

    [ObservableProperty]
    private string _searchKeyword = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _notificationText = string.Empty;

    public ProjectsViewModel(IE3dProjectService projectService, IE3dLauncherService launcherService, MainWindowViewModel mainVm)
    {
        _projectService = projectService;
        _launcherService = launcherService;
        _mainVm = mainVm;

        LoadProjectsCommand.Execute(null);
    }

    [RelayCommand]
    public async Task LoadProjectsAsync()
    {
        IsLoading = true;
        NotificationText = Strings.Projects_Scanning;

        try
        {
            var list = await _projectService.LoadAllProjectsAsync();
            Projects = new ObservableCollection<ProjectItem>(list);
            ApplyFilter();
            NotificationText = string.Format(Strings.Projects_Loaded, Projects.Count);
        }
        catch (Exception ex)
        {
            NotificationText = string.Format(Strings.Projects_ScanError, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchKeywordChanged(string value) => ApplyFilter();
    partial void OnFilterTabChanged(string value) => ApplyFilter();

    public void ApplyFilter()
    {
        var q = Projects.AsEnumerable();

        if (FilterTab == "Favorites")
            q = q.Where(p => p.IsFavorite);
        else if (FilterTab == "Local")
            q = q.Where(p => !p.IsUnc);
        else if (FilterTab == "Unc")
            q = q.Where(p => p.IsUnc);

        if (!string.IsNullOrWhiteSpace(SearchKeyword))
        {
            string kw = SearchKeyword.Trim();
            q = q.Where(p => p.Code.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                             p.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                             p.Path.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        FilteredProjects = new ObservableCollection<ProjectItem>(q);
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(ProjectItem? item)
    {
        if (item == null) return;
        bool isFav = await _projectService.ToggleFavoriteAsync(item.Code);
        item.IsFavorite = isFav;
        ApplyFilter();
    }

    [RelayCommand]
    private async Task SetActiveAndLaunchAsync(ProjectItem? item)
    {
        if (item == null) return;
        IsLoading = true;
        NotificationText = string.Format(Strings.Projects_SwitchingTo, item.Code);

        var res = await _launcherService.SwitchAndLaunchAsync(item);
        NotificationText = res.Message;

        foreach (var p in Projects)
        {
            p.IsActive = p.Code.Equals(item.Code, StringComparison.OrdinalIgnoreCase);
        }

        _mainVm.RefreshActiveProject();
        IsLoading = false;
    }

    [RelayCommand]
    private void OpenProjectFolder(ProjectItem? item)
    {
        if (item == null) return;
        _projectService.OpenFolder(item.Path);
    }
}
