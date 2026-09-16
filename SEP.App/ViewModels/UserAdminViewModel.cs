using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using E3dAdmin.Models;
using SEP.App.Resources;
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
    private string _statusMessage = Strings.Common_Ready;

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
        StatusMessage = string.Format(Strings.Users_Loading, SelectedProject);

        try
        {
            var userList = await _adminBridge.GetUsersAsync(SelectedProject);
            var teamList = await _adminBridge.GetTeamsAsync(SelectedProject);

            Users = new ObservableCollection<UserInfo>(userList);
            Teams = new ObservableCollection<TeamInfo>(teamList);

            StatusMessage = string.Format(Strings.Users_Loaded, SelectedProject, Users.Count, Teams.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(Strings.Users_LoadFailed, ex.Message);
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
            StatusMessage = Strings.Users_InputRequired;
            return;
        }

        IsLoading = true;
        StatusMessage = string.Format(Strings.Users_Adding, SelectedProject, NewUserName.ToUpperInvariant());

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
        StatusMessage = string.Format(Strings.Users_Deleting, user.Name);

        var res = await _adminBridge.DeleteUserAsync(SelectedProject, user.Name, force: false);
        StatusMessage = res.Message;

        if (res.Success)
        {
            await RefreshUsersAsync();
        }

        IsLoading = false;
    }
}
