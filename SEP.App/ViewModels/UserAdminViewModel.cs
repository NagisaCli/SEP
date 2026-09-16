using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using E3dAdmin.Models;
using SEP.App.Resources;
using SEP.App.Services;

namespace SEP.App.ViewModels;

/// <summary>A project the AVEVA admin tool can be pointed at: needs the real project code (the XXX of "set XXX000=").</summary>
public sealed record ProjectChoice(string Code, string Name)
{
    public string Display => string.Equals(Code, Name, StringComparison.OrdinalIgnoreCase) ? Code : $"{Code} · {Name}";
    public override string ToString() => Display;
}

public partial class UserAdminViewModel : ObservableObject
{
    private readonly IE3dAdminBridge _adminBridge;
    private readonly IProjectCatalog _catalog;

    [ObservableProperty]
    private ObservableCollection<ProjectChoice> _availableProjects = new();

    [ObservableProperty]
    private ProjectChoice? _selectedProject;

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

    public UserAdminViewModel(IE3dAdminBridge adminBridge, IProjectCatalog catalog)
    {
        _adminBridge = adminBridge;
        _catalog = catalog;

        LoadProjectList();
        _catalog.Changed += (_, _) =>
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) LoadProjectList(); else d.BeginInvoke(LoadProjectList);
        };
    }

    public void LoadProjectList()
    {
        // One entry per project code; the active project first, then my projects, then the rest.
        var choices = _catalog.Projects
            .Where(p => !string.IsNullOrEmpty(p.Code))
            .OrderByDescending(p => p.IsActive).ThenByDescending(p => p.IsFavorite).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .GroupBy(p => p.Code!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProjectChoice(g.Key.ToUpperInvariant(), g.First().Name))
            .ToList();

        var current = SelectedProject;
        AvailableProjects = new ObservableCollection<ProjectChoice>(choices);
        if (choices.Count == 0)
        {
            SelectedProject = null;
            StatusMessage = Strings.Users_NoCode;
            return;
        }
        SelectedProject = current != null ? choices.FirstOrDefault(c => c.Code == current.Code) ?? choices[0] : choices[0];
    }

    partial void OnSelectedProjectChanged(ProjectChoice? value)
    {
        if (value != null) RefreshUsersCommand.Execute(null);
    }

    [RelayCommand]
    public async Task RefreshUsersAsync()
    {
        var project = SelectedProject;
        if (project == null) return;

        IsLoading = true;
        StatusMessage = string.Format(Strings.Users_Loading, project.Code);

        try
        {
            var userList = await _adminBridge.GetUsersAsync(project.Code);
            var teamList = await _adminBridge.GetTeamsAsync(project.Code);

            Users = new ObservableCollection<UserInfo>(userList);
            Teams = new ObservableCollection<TeamInfo>(teamList);

            StatusMessage = string.Format(Strings.Users_Loaded, project.Code, Users.Count, Teams.Count);
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
        var project = SelectedProject;
        if (project == null) return;
        if (string.IsNullOrWhiteSpace(NewUserName) || string.IsNullOrWhiteSpace(NewUserTeam))
        {
            StatusMessage = Strings.Users_InputRequired;
            return;
        }

        IsLoading = true;
        StatusMessage = string.Format(Strings.Users_Adding, project.Code, NewUserName.ToUpperInvariant());

        var res = await _adminBridge.AddUserAsync(
            project.Code,
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
        var project = SelectedProject;
        if (user == null || project == null) return;

        IsLoading = true;
        StatusMessage = string.Format(Strings.Users_Deleting, user.Name);

        var res = await _adminBridge.DeleteUserAsync(project.Code, user.Name, force: false);
        StatusMessage = res.Message;

        if (res.Success)
        {
            await RefreshUsersAsync();
        }

        IsLoading = false;
    }
}
