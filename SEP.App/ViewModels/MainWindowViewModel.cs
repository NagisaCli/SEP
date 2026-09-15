using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Models;
using SEP.App.Services;

namespace SEP.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IE3dProjectService _projectService;
    private readonly IE3dLauncherService _launcherService;

    [ObservableProperty]
    private string _activeProjectCode = "无活动项目";

    [ObservableProperty]
    private string _activeProjectStatus = "待选择";

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    public MainWindowViewModel(IE3dProjectService projectService, IE3dLauncherService launcherService)
    {
        _projectService = projectService;
        _launcherService = launcherService;

        RefreshActiveProject();
    }

    public void RefreshActiveProject()
    {
        string? active = _projectService.ProjectsConfig.LastActiveProject;
        if (!string.IsNullOrEmpty(active))
        {
            ActiveProjectCode = active;
            ActiveProjectStatus = "就绪";
        }
        else
        {
            ActiveProjectCode = "未指定活动工程";
            ActiveProjectStatus = "待选择";
        }
    }

    [RelayCommand]
    private async Task LaunchActiveProjectAsync()
    {
        if (string.IsNullOrEmpty(_projectService.ProjectsConfig.LastActiveProject))
        {
            StatusMessage = "请先在工程工作台中选择一个工程！";
            return;
        }

        string code = _projectService.ProjectsConfig.LastActiveProject;
        var projects = await _projectService.LoadAllProjectsAsync();
        var proj = projects.Find(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

        if (proj == null)
        {
            StatusMessage = $"未找到活动工程 [{code}] 的物理路径配置";
            return;
        }

        IsBusy = true;
        StatusMessage = $"正在切换环境并唤起 AVEVA E3D [{code}]...";

        var res = await _launcherService.SwitchAndLaunchAsync(proj);
        StatusMessage = res.Message;
        IsBusy = false;
    }
}
