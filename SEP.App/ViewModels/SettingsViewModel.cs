using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SEP.App.Services;

namespace SEP.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IE3dProjectService _projectService;

    [ObservableProperty]
    private string _installDir = string.Empty;

    [ObservableProperty]
    private string _projectsDir = string.Empty;

    [ObservableProperty]
    private string _evarsBat = string.Empty;

    [ObservableProperty]
    private string _e3dVersion = string.Empty;

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    public SettingsViewModel(IE3dProjectService projectService)
    {
        _projectService = projectService;

        var paths = _projectService.PathsConfig;
        InstallDir = paths.InstallDir ?? string.Empty;
        ProjectsDir = paths.ProjectsDir ?? string.Empty;
        EvarsBat = paths.EvarsBat ?? string.Empty;
        E3dVersion = paths.E3dVersion ?? "AVEVA Everything3D 3.1";

        AutoStart = _projectService.ProjectsConfig.Settings.AutoStart;
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        _projectService.PathsConfig.InstallDir = InstallDir.Trim();
        _projectService.PathsConfig.ProjectsDir = ProjectsDir.Trim();
        _projectService.PathsConfig.EvarsBat = EvarsBat.Trim();
        _projectService.PathsConfig.E3dVersion = E3dVersion.Trim();

        _projectService.ProjectsConfig.Settings.AutoStart = AutoStart;

        await _projectService.SaveConfigAsync();
        StatusMessage = "设置保存成功！";
    }
}
