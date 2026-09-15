using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using E3dAdmin.Models;
using SEP.App.Services;

namespace SEP.App.ViewModels;

public partial class UserAdminViewModel : ObservableObject
{
    private readonly IE3dAdminBridge _adminBridge;
    private readonly IE3dProjectService _projectService;

    [ObservableProperty]
    private ObservableCollection<string> _availableProjects = new();

    [ObservableProperty]
    private string _selectedProject = string.Empty;

    [ObservableProperty]
    private ObservableCollection<UserInfo> _users = new();

    [ObservableProperty]
    private ObservableCollection<TeamInfo> _teams = new();

    [ObservableProperty]
    private UserInfo? _selectedUser;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    // New User Inputs
    [ObservableProperty]
    private string _newUserName = string.Empty;

    [ObservableProperty]
    private string _newUserTeam = string.Empty;

    [ObservableProperty]
    private string _newUserPassword = string.Empty;

    [ObservableProperty]
    private string _newUserSecurity = "General";

    [ObservableProperty]
    private string _newUserDesc = string.Empty;

    public UserAdminViewModel(IE3dAdminBridge adminBridge, IE3dProjectService projectService)
    {
        _adminBridge = adminBridge;
        _projectService = projectService;

        LoadProjectList();
    }

    public void LoadProjectList()
    {
        AvailableProjects.Clear();
        foreach (var kv in _projectService.ProjectsConfig.Projects)
        {
            AvailableProjects.Add(kv.Key);
        }

        if (AvailableProjects.Count > 0 && string.IsNullOrEmpty(SelectedProject))
        {
            SelectedProject = AvailableProjects[0];
        }
    }

    partial void OnSelectedProjectChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            RefreshUsersCommand.Execute(null);
        }
    }

    [RelayCommand]
    public async Task RefreshUsersAsync()
    {
        if (string.IsNullOrEmpty(SelectedProject)) return;

        IsLoading = true;
        StatusMessage = $"正在读取 [{SelectedProject}] 用户与团队清单...";

        try
        {
            var userList = await _adminBridge.GetUsersAsync(SelectedProject);
            var teamList = await _adminBridge.GetTeamsAsync(SelectedProject);

            Users = new ObservableCollection<UserInfo>(userList);
            Teams = new ObservableCollection<TeamInfo>(teamList);

            StatusMessage = $"[{SelectedProject}] 共找到 {Users.Count} 名用户与 {Teams.Count} 个团队。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task AddUserAsync()
    {
        if (string.IsNullOrWhiteSpace(NewUserName) || string.IsNullOrWhiteSpace(NewUserTeam))
        {
            StatusMessage = "请输入用户名和初始团队！";
            return;
        }

        IsLoading = true;
        StatusMessage = $"正在向 [{SelectedProject}] 添加用户 [{NewUserName.ToUpperInvariant()}]...";

        var res = await _adminBridge.AddUserAsync(
            SelectedProject,
            NewUserName.Trim().ToUpperInvariant(),
            NewUserTeam.Trim().ToUpperInvariant(),
            string.IsNullOrWhiteSpace(NewUserPassword) ? null : NewUserPassword.Trim(),
            NewUserSecurity,
            NewUserDesc
        );

        StatusMessage = res.Message;
        if (res.Success)
        {
            NewUserName = string.Empty;
            NewUserPassword = string.Empty;
            NewUserDesc = string.Empty;
            await RefreshUsersAsync();
        }

        IsLoading = false;
    }

    [RelayCommand]
    private async Task DeleteUserAsync(UserInfo? user)
    {
        if (user == null) return;

        IsLoading = true;
        StatusMessage = $"正在删除用户 [{user.Name}]...";

        var res = await _adminBridge.DeleteUserAsync(SelectedProject, user.Name, force: false);
        StatusMessage = res.Message;

        if (res.Success)
        {
            await RefreshUsersAsync();
        }

        IsLoading = false;
    }
}
