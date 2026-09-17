using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using E3dAdmin.Models;
using SEP.App.Models;
using SEP.App.Resources;
using SEP.App.Services;

namespace SEP.App.ViewModels;

/// <summary>A project the AVEVA admin tool can be pointed at: needs the real project code (the XXX of "set XXX000=").</summary>
public sealed class ProjectChoice
{
    public ProjectChoice(ProjectItem project, bool codeIsShared)
    {
        Project = project;
        Code = (project.Code ?? project.Name).ToUpperInvariant();
        Name = project.DisplayName is { Length: > 0 } dn ? dn : project.Name;
        string head = string.Equals(Code, Name, StringComparison.OrdinalIgnoreCase) ? Code : $"{Code} · {Name}";
        // The same code can exist in several libraries (a server copy and a local one): say which library this is.
        Display = codeIsShared ? $"{head}  —  {project.LibraryName}" : head;
    }

    public ProjectItem Project { get; }
    public string Code { get; }
    public string Name { get; }
    public string Display { get; }
    public override string ToString() => Display;
}

/// <summary>
/// Users &amp; teams page: per-project administrator credentials, the user and team lists (cached first, then
/// refreshed through ADMIN), and every management action with confirmation where it destroys something.
/// </summary>
public partial class UserAdminViewModel : ObservableObject
{
    private static readonly TimeSpan CacheFreshFor = TimeSpan.FromMinutes(10);

    private readonly IE3dAdminBridge _adminBridge;
    private readonly IProjectCatalog _catalog;
    private readonly AdminCredentialStore _credentials;
    private readonly AdminListingCache _cache;
    private readonly IDialogService _dialogs;

    private List<UserRow> _allUsers = new();
    private List<TeamRow> _allTeams = new();
    private int _loadSerial;
    private bool _suppressReload;

    // ── project ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<ProjectChoice> _availableProjects = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    private ProjectChoice? _selectedProject;

    public bool HasProject => SelectedProject != null;

    // ── credentials ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _showCredentials;

    [ObservableProperty]
    private string _adminUser = "SYSTEM";

    [ObservableProperty]
    private string _adminPassword = string.Empty;

    /// <summary>"SYSTEM · saved" / "SYSTEM · default" summary for the credentials button.</summary>
    [ObservableProperty]
    private string _credentialSummary = string.Empty;

    [ObservableProperty]
    private bool _hasSavedCredentials;

    [ObservableProperty]
    private string _credentialStatus = string.Empty;

    [ObservableProperty]
    private bool _credentialStatusIsError;

    [ObservableProperty]
    private bool _isTestingCredentials;

    // ── listing ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<UserRow> _users = new();

    [ObservableProperty]
    private ObservableCollection<TeamRow> _teams = new();

    [ObservableProperty]
    private string _userFilter = string.Empty;

    [ObservableProperty]
    private string _teamFilter = string.Empty;

    [ObservableProperty]
    private bool _isUsersTab = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateUserCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateTeamCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasLoaded;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>The lists on screen come from the cache and have not been confirmed by ADMIN in this session yet.</summary>
    [ObservableProperty]
    private bool _isFromCache;

    [ObservableProperty]
    private string _listedAtText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = Strings.Common_Ready;

    [ObservableProperty]
    private bool _statusIsError;

    public int UserCount => _allUsers.Count;
    public int TeamCount => _allTeams.Count;
    public int FreeUserCount => _allUsers.Count(u => u.IsFree);

    public UserAdminViewModel(IE3dAdminBridge adminBridge, IProjectCatalog catalog, AdminCredentialStore credentials,
        AdminListingCache cache, IDialogService dialogs)
    {
        _adminBridge = adminBridge;
        _catalog = catalog;
        _credentials = credentials;
        _cache = cache;
        _dialogs = dialogs;

        LoadProjectList();
        _catalog.Changed += (_, _) =>
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) LoadProjectList(); else d.BeginInvoke(LoadProjectList);
        };
    }

    // ── projects ─────────────────────────────────────────────────────────────────

    public void LoadProjectList()
    {
        // Every project with a readable code; the active project first, then my projects, then the rest.
        var withCode = _catalog.Projects.Where(p => !string.IsNullOrEmpty(p.Code)).ToList();
        var shared = withCode.GroupBy(p => p.Code!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = withCode
            .OrderByDescending(p => p.IsActive).ThenByDescending(p => p.IsFavorite).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ProjectChoice(p, shared.Contains(p.Code!)))
            .ToList();

        var current = SelectedProject;
        AvailableProjects = new ObservableCollection<ProjectChoice>(choices);
        if (choices.Count == 0)
        {
            SelectedProject = null;
            SetStatus(Strings.Users_NoCode, error: false);
            return;
        }
        var same = current != null ? choices.FirstOrDefault(c => c.Project.Id == current.Project.Id) : null;
        if (same != null)
        {
            // Same project after a catalog refresh: swap the choice object without reloading the page.
            _suppressReload = true;
            try { SelectedProject = same; }
            finally { _suppressReload = false; }
            return;
        }
        SelectedProject = current != null ? choices.FirstOrDefault(c => c.Code == current.Code) ?? choices[0] : choices[0];
    }

    partial void OnSelectedProjectChanged(ProjectChoice? value)
    {
        if (_suppressReload) return;
        RefreshCredentialSummary();
        CredentialStatus = string.Empty;
        _allUsers = new();
        _allTeams = new();
        ApplyFilters();
        HasLoaded = false;
        IsFromCache = false;
        ListedAtText = string.Empty;
        if (value != null) _ = LoadAsync(force: false);
    }

    // ── credentials ──────────────────────────────────────────────────────────────

    private void RefreshCredentialSummary()
    {
        var project = SelectedProject;
        if (project == null)
        {
            CredentialSummary = string.Empty;
            HasSavedCredentials = false;
            return;
        }
        var saved = _credentials.Get(project.Code);
        HasSavedCredentials = saved != null;
        var cred = saved ?? AdminCredential.Default;
        AdminUser = cred.User;
        AdminPassword = cred.Password;
        CredentialSummary = saved != null
            ? string.Format(Strings.Users_CredSaved, cred.User)
            : string.Format(Strings.Users_CredDefault, cred.User);
    }

    [RelayCommand]
    private void ToggleCredentials() => ShowCredentials = !ShowCredentials;

    [RelayCommand]
    private async Task TestCredentialsAsync()
    {
        var project = SelectedProject;
        if (project == null) return;
        if (string.IsNullOrWhiteSpace(AdminUser))
        {
            SetCredentialStatus(Strings.Users_CredUserRequired, error: true);
            return;
        }
        IsTestingCredentials = true;
        SetCredentialStatus(Strings.Users_CredTesting, error: false);
        var res = await _adminBridge.VerifyCredentialsAsync(project.Project, AdminUser.Trim(), AdminPassword);
        IsTestingCredentials = false;
        SetCredentialStatus(res.Message, error: !res.Success);
    }

    [RelayCommand]
    private async Task SaveCredentialsAsync()
    {
        var project = SelectedProject;
        if (project == null) return;
        if (string.IsNullOrWhiteSpace(AdminUser))
        {
            SetCredentialStatus(Strings.Users_CredUserRequired, error: true);
            return;
        }
        IsTestingCredentials = true;
        SetCredentialStatus(Strings.Users_CredTesting, error: false);
        var res = await _adminBridge.VerifyCredentialsAsync(project.Project, AdminUser.Trim(), AdminPassword);
        IsTestingCredentials = false;
        if (!res.Success)
        {
            // Let the user keep a login ADMIN rejected right now (e.g. the share is offline), but say so.
            bool keep = await _dialogs.ConfirmAsync(Strings.Users_CredSaveAnywayTitle,
                string.Format(Strings.Users_CredSaveAnywayBody, res.Message), Strings.Users_CredSaveAnyway, danger: true);
            if (!keep)
            {
                SetCredentialStatus(res.Message, error: true);
                return;
            }
        }
        try
        {
            _credentials.Set(project.Code, AdminUser.Trim().ToUpperInvariant(), AdminPassword);
        }
        catch (Exception ex)
        {
            SetCredentialStatus(string.Format(Strings.Users_CredSaveFailed, ex.Message), error: true);
            return;
        }
        RefreshCredentialSummary();
        SetCredentialStatus(string.Format(Strings.Users_CredSavedMsg, AdminUser.Trim().ToUpperInvariant(), project.Code), error: false);
        if (res.Success)
        {
            ShowCredentials = false;
            await LoadAsync(force: true);
        }
    }

    [RelayCommand]
    private void ForgetCredentials()
    {
        var project = SelectedProject;
        if (project == null) return;
        _credentials.Remove(project.Code);
        RefreshCredentialSummary();
        SetCredentialStatus(string.Format(Strings.Users_CredForgotten, project.Code), error: false);
    }

    private void SetCredentialStatus(string text, bool error)
    {
        CredentialStatus = text;
        CredentialStatusIsError = error;
    }

    // ── listing ──────────────────────────────────────────────────────────────────

    private bool NotBusy() => !IsBusy && SelectedProject != null;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task RefreshAsync() => LoadAsync(force: true);

    /// <summary>Shows the cached listing at once and asks ADMIN unless the cache is fresh (or <paramref name="force"/>).</summary>
    private async Task LoadAsync(bool force)
    {
        var project = SelectedProject;
        if (project == null) return;
        int serial = ++_loadSerial;

        var cached = _cache.Get(project.Code);
        if (cached != null && !force)
        {
            Show(cached.Users, cached.Teams, fromCache: true, cached.UpdatedAt);
            if (cached.UpdatedAt != null && DateTime.Now - cached.UpdatedAt.Value < CacheFreshFor)
            {
                SetStatus(string.Format(Strings.Users_CachedFresh, project.Code, cached.Users.Count, cached.Teams.Count, FormatWhen(cached.UpdatedAt)), error: false);
                return;
            }
            SetStatus(string.Format(Strings.Users_CachedShown, project.Code, FormatWhen(cached.UpdatedAt)), error: false);
        }

        IsBusy = true;
        SetStatus(string.Format(Strings.Users_Loading, project.Code), error: false);
        try
        {
            var (users, teams) = await _adminBridge.GetUsersAndTeamsAsync(project.Project);
            if (serial != _loadSerial) return;   // the user switched project meanwhile
            _cache.Set(project.Code, users, teams);
            Show(users, teams, fromCache: false, DateTime.Now);
            SetStatus(string.Format(Strings.Users_Loaded, project.Code, users.Count, teams.Count), error: false);
        }
        catch (Exception ex)
        {
            if (serial != _loadSerial) return;
            var fail = E3dAdminBridge.Fail(ex, project.Project);
            SetStatus(fail.Message, error: true);
            if (fail.Error == AdminErrorKind.ConnectionFailed && !HasSavedCredentials) ShowCredentials = true;
        }
        finally
        {
            if (serial == _loadSerial) IsBusy = false;
        }
    }

    private void Show(List<UserInfo> users, List<TeamInfo> teams, bool fromCache, DateTime? at)
    {
        _allUsers = users.Select(u => new UserRow(u)).ToList();
        _allTeams = teams.Select(t => new TeamRow(t)).ToList();
        HasLoaded = true;
        IsFromCache = fromCache;
        ListedAtText = at == null ? string.Empty : string.Format(fromCache ? Strings.Users_ListedCached : Strings.Users_ListedLive, FormatWhen(at));
        ApplyFilters();
    }

    private static string FormatWhen(DateTime? at) => at == null ? "?" : at.Value.ToString("g");

    partial void OnUserFilterChanged(string value) => ApplyFilters();
    partial void OnTeamFilterChanged(string value) => ApplyFilters();

    private void ApplyFilters()
    {
        Users = new ObservableCollection<UserRow>(_allUsers.Where(u => u.Matches(UserFilter)));
        Teams = new ObservableCollection<TeamRow>(_allTeams.Where(t => t.Matches(TeamFilter)));
        IsEmpty = HasLoaded && _allUsers.Count == 0 && _allTeams.Count == 0;
        OnPropertyChanged(nameof(UserCount));
        OnPropertyChanged(nameof(TeamCount));
        OnPropertyChanged(nameof(FreeUserCount));
    }

    private void SetStatus(string text, bool error)
    {
        StatusMessage = text;
        StatusIsError = error;
    }

    private IReadOnlyList<string> TeamLabels() => _allTeams.Select(t => t.Label).ToList();
    private IReadOnlyList<string> UserNames() => _allUsers.Select(u => u.Name).ToList();

    /// <summary>Runs one change with the row marked busy, reports the outcome and reloads the listing on success.</summary>
    private async Task RunChangeAsync(ObservableObject? row, Func<Task<AdminResult>> action, string progress)
    {
        var project = SelectedProject;
        if (project == null) return;
        SetRowBusy(row, true);
        IsBusy = true;
        SetStatus(progress, error: false);
        AdminResult res;
        try { res = await action(); }
        finally { IsBusy = false; SetRowBusy(row, false); }
        SetStatus(res.Message, error: !res.Success);
        if (res.Success)
        {
            _cache.Invalidate(project.Code);
            await LoadAsync(force: true);
        }
        else if (res.Error == AdminErrorKind.ConnectionFailed)
        {
            ShowCredentials = true;
        }
    }

    private static void SetRowBusy(ObservableObject? row, bool busy)
    {
        if (row is UserRow u) u.IsBusy = busy;
        else if (row is TeamRow t) t.IsBusy = busy;
    }

    // ── user actions ─────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CreateUserAsync()
    {
        var project = SelectedProject;
        if (project == null) return;
        var draft = await _dialogs.NewUserAsync(TeamLabels(), project.Code);
        if (draft == null) return;
        await RunChangeAsync(null,
            () => _adminBridge.AddUserAsync(project.Project, draft.Name, draft.Team, draft.Password, draft.Security, draft.Description),
            string.Format(Strings.Users_Adding, project.Code, draft.Name));
    }

    [RelayCommand]
    private async Task DeleteUserAsync(UserRow? user)
    {
        var project = SelectedProject;
        if (user == null || project == null) return;
        bool ok = await _dialogs.ConfirmAsync(Strings.Users_DeleteTitle,
            string.Format(Strings.Users_DeleteBody, user.Name, project.Code), Strings.Users_DeleteConfirm, danger: true);
        if (!ok) return;
        await RunChangeAsync(user, () => _adminBridge.DeleteUserAsync(project.Project, user.Name), string.Format(Strings.Users_Deleting, user.Name));
    }

    [RelayCommand]
    private async Task SetPasswordAsync(UserRow? user)
    {
        var project = SelectedProject;
        if (user == null || project == null) return;
        var res = await _dialogs.PromptAsync(new PromptRequest
        {
            Title = string.Format(Strings.Users_PasswordTitle, user.Name),
            Message = string.Format(Strings.Users_PasswordBody, user.Name, project.Code),
            PasswordLabel = Strings.Users_PasswordLabel,
            OkLabel = Strings.Users_PasswordConfirm,
        });
        if (res == null) return;
        await RunChangeAsync(user, () => _adminBridge.SetPasswordAsync(project.Project, user.Name, res.Password), string.Format(Strings.Users_ChangingPassword, user.Name));
        if (user.Name.Equals(AdminUser, StringComparison.OrdinalIgnoreCase) && HasSavedCredentials)
        {
            // The administrator changed its own password: keep the saved login usable.
            _credentials.Set(project.Code, AdminUser, res.Password);
            RefreshCredentialSummary();
        }
    }

    [RelayCommand]
    private async Task ToggleSecurityAsync(UserRow? user)
    {
        var project = SelectedProject;
        if (user == null || project == null) return;
        string target = user.IsFree ? "General" : "Free";
        bool ok = await _dialogs.ConfirmAsync(Strings.Users_SecurityTitle,
            string.Format(user.IsFree ? Strings.Users_SecurityToGeneralBody : Strings.Users_SecurityToFreeBody, user.Name),
            string.Format(Strings.Users_SecurityConfirm, target.ToUpperInvariant()), danger: user.IsFree);
        if (!ok) return;
        await RunChangeAsync(user, () => _adminBridge.SetSecurityAsync(project.Project, user.Name, target), string.Format(Strings.Users_ChangingSecurity, user.Name));
    }

    [RelayCommand]
    private async Task EditDescriptionAsync(UserRow? user)
    {
        var project = SelectedProject;
        if (user == null || project == null) return;
        var res = await _dialogs.PromptAsync(new PromptRequest
        {
            Title = string.Format(Strings.Users_DescriptionTitle, user.Name),
            TextLabel = Strings.Users_NewDescLabel,
            TextPlaceholder = Strings.Users_NewDescPlaceholder,
            TextInitial = user.Description,
            OkLabel = Strings.Common_Save,
        });
        if (res == null) return;
        await RunChangeAsync(user, () => _adminBridge.SetDescriptionAsync(project.Project, user.Name, res.Text), string.Format(Strings.Users_UpdatingDescription, user.Name));
    }

    [RelayCommand]
    private async Task AddToTeamAsync(UserRow? user)
    {
        var project = SelectedProject;
        if (user == null || project == null) return;
        var candidates = TeamLabels().Where(t => !user.Teams.Any(x => x.TrimStart('*').Equals(t, StringComparison.OrdinalIgnoreCase))).ToList();
        var res = await _dialogs.PromptAsync(new PromptRequest
        {
            Title = string.Format(Strings.Users_AddToTeamTitle, user.Name),
            ChoiceLabel = Strings.Users_TeamLabel,
            Choices = candidates,
            ChoiceInitial = candidates.FirstOrDefault(),
            TextLabel = candidates.Count == 0 ? Strings.Users_TeamNameLabel : null,
            TextPlaceholder = Strings.Users_TeamNamePlaceholder,
            TextUpperCase = true,
            OkLabel = Strings.Users_AddToTeamConfirm,
        });
        if (res == null) return;
        string team = (res.Choice ?? res.Text).Trim();
        if (team.Length == 0) return;
        await RunChangeAsync(user, () => _adminBridge.AddUserToTeamAsync(project.Project, team, user.Name), string.Format(Strings.Users_AddingToTeam, user.Name, team));
    }

    [RelayCommand]
    private async Task RemoveMembershipAsync(TeamMembership? link)
    {
        var project = SelectedProject;
        if (link == null || project == null) return;
        bool ok = await _dialogs.ConfirmAsync(Strings.Users_RemoveMemberTitle,
            string.Format(Strings.Users_RemoveMemberBody, link.User, link.TeamLabel), Strings.Users_RemoveMemberConfirm, danger: true);
        if (!ok) return;
        var row = (ObservableObject?)_allUsers.FirstOrDefault(u => u.Name == link.User) ?? _allTeams.FirstOrDefault(t => t.Name == link.Team);
        await RunChangeAsync(row, () => _adminBridge.RemoveUserFromTeamAsync(project.Project, link.TeamLabel, link.User),
            string.Format(Strings.Users_RemovingMember, link.User, link.TeamLabel));
    }

    // ── team actions ─────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CreateTeamAsync()
    {
        var project = SelectedProject;
        if (project == null) return;
        var res = await _dialogs.PromptAsync(new PromptRequest
        {
            Title = string.Format(Strings.Users_NewTeamTitle, project.Code),
            TextLabel = Strings.Users_TeamNameLabel,
            TextPlaceholder = Strings.Users_TeamNamePlaceholder,
            TextUpperCase = true,
            TextMaxLength = 32,
            OkLabel = Strings.Users_NewTeamConfirm,
        });
        if (res == null || res.Text.Trim().Length == 0) return;
        string team = res.Text.Trim().TrimStart('*').ToUpperInvariant();
        await RunChangeAsync(null, () => _adminBridge.CreateTeamAsync(project.Project, team, null), string.Format(Strings.Users_CreatingTeam, team));
    }

    [RelayCommand]
    private async Task DeleteTeamAsync(TeamRow? team)
    {
        var project = SelectedProject;
        if (team == null || project == null) return;
        bool ok = await _dialogs.ConfirmAsync(Strings.Users_DeleteTeamTitle,
            string.Format(Strings.Users_DeleteTeamBody, team.Label, team.MemberCount), Strings.Users_DeleteTeamConfirm, danger: true);
        if (!ok) return;
        await RunChangeAsync(team, () => _adminBridge.DeleteTeamAsync(project.Project, team.Label), string.Format(Strings.Users_DeletingTeam, team.Label));
    }

    [RelayCommand]
    private async Task AddMemberAsync(TeamRow? team)
    {
        var project = SelectedProject;
        if (team == null || project == null) return;
        var candidates = UserNames().Where(u => !team.Members.Contains(u, StringComparer.OrdinalIgnoreCase)).ToList();
        var res = await _dialogs.PromptAsync(new PromptRequest
        {
            Title = string.Format(Strings.Users_AddMemberTitle, team.Label),
            ChoiceLabel = Strings.Users_UserLabel,
            Choices = candidates,
            ChoiceInitial = candidates.FirstOrDefault(),
            TextLabel = candidates.Count == 0 ? Strings.Users_NewNameLabel : null,
            TextPlaceholder = Strings.Users_NewNamePlaceholder,
            TextUpperCase = true,
            OkLabel = Strings.Users_AddMemberConfirm,
        });
        if (res == null) return;
        string user = (res.Choice ?? res.Text).Trim().ToUpperInvariant();
        if (user.Length == 0) return;
        await RunChangeAsync(team, () => _adminBridge.AddUserToTeamAsync(project.Project, team.Label, user), string.Format(Strings.Users_AddingToTeam, user, team.Label));
    }

    [RelayCommand]
    private void ShowUsersTab() => IsUsersTab = true;

    [RelayCommand]
    private void ShowTeamsTab() => IsUsersTab = false;
}
