using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using E3dAdmin.Models;
using E3dAdmin.Services;
using SEP.App.Models;
using SEP.App.Resources;

namespace SEP.App.Services;

public class E3dAdminBridge : IE3dAdminBridge
{
    private readonly AvevaAdminService _adminService;
    private readonly AdminCredentialStore _credentials;

    public E3dAdminBridge(AdminCredentialStore credentials)
    {
        _credentials = credentials;
        _adminService = new AvevaAdminService(new AvevaProcessRunner());
    }

    // ── context ──────────────────────────────────────────────────────────────────

    private AdminContext Context(ProjectItem project, AdminCredential? credential = null)
    {
        string code = project.Code ?? project.Name;
        credential ??= _credentials.Resolve(code);
        var ctx = new AdminContext
        {
            Project = code.ToUpperInvariant(),
            AdminUser = credential.User,
            AdminPassword = credential.Password,
        };
        // The project's evars tell adm.exe where the databases are; without them only projects that the
        // ADMIN install's custom_evars.bat happens to register can be opened.
        var (_, vars) = ProjectEvars.Parse(project.BatPath, LibraryPathOf(project));
        foreach (var kv in vars) ctx.Environment[kv.Key] = kv.Value;
        return ctx;
    }

    private static string? LibraryPathOf(ProjectItem project)
    {
        // A project folder inside a library: the library is the parent of the project folder; a bat in the
        // library root: the library is the bat's folder.
        string dir = project.ProjectDir;
        if (string.IsNullOrEmpty(dir)) return null;
        string? batDir = System.IO.Path.GetDirectoryName(project.BatPath);
        return string.Equals(batDir, dir, StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetDirectoryName(dir)
            : dir;
    }

    // ── queries ──────────────────────────────────────────────────────────────────

    public async Task<AdminResult> VerifyCredentialsAsync(ProjectItem project, string adminUser, string adminPassword)
    {
        try
        {
            await _adminService.VerifyCredentialsAsync(Context(project, new AdminCredential(adminUser, adminPassword)));
            return AdminResult.Ok(string.Format(Strings.Admin_VerifyOk, adminUser.ToUpperInvariant(), project.Code ?? project.Name));
        }
        catch (Exception ex)
        {
            return Fail(ex, project);
        }
    }

    public Task<(List<UserInfo> Users, List<TeamInfo> Teams)> GetUsersAndTeamsAsync(ProjectItem project)
        => _adminService.ListUsersAndTeamsAsync(Context(project));

    // ── users ────────────────────────────────────────────────────────────────────

    public Task<AdminResult> AddUserAsync(ProjectItem project, string username, string? team, string? password, string security, string? description)
        => Run(project,
            () => _adminService.AddUserAsync(Context(project), username, team, password, security, description),
            string.IsNullOrWhiteSpace(team)
                ? string.Format(Strings.Admin_UserCreatedNoTeam, username)
                : string.Format(Strings.Admin_UserCreated, username, team));

    public Task<AdminResult> DeleteUserAsync(ProjectItem project, string username)
        => Run(project, () => _adminService.DeleteUserAsync(Context(project), username), string.Format(Strings.Admin_UserDeleted, username));

    public Task<AdminResult> SetPasswordAsync(ProjectItem project, string username, string newPassword)
        => Run(project, () => _adminService.SetPasswordAsync(Context(project), username, newPassword), string.Format(Strings.Admin_PasswordChanged, username));

    public Task<AdminResult> SetSecurityAsync(ProjectItem project, string username, string security)
        => Run(project, () => _adminService.SetSecurityAsync(Context(project), username, security),
            string.Format(Strings.Admin_SecurityChanged, username, security.ToUpperInvariant()));

    public Task<AdminResult> SetDescriptionAsync(ProjectItem project, string username, string description)
        => Run(project, () => _adminService.SetDescriptionAsync(Context(project), username, description), string.Format(Strings.Admin_DescriptionChanged, username));

    // ── teams ────────────────────────────────────────────────────────────────────

    public Task<AdminResult> AddUserToTeamAsync(ProjectItem project, string team, string username)
        => Run(project, () => _adminService.AddUserToTeamAsync(Context(project), team, username), string.Format(Strings.Admin_UserAddedToTeam, username, team));

    public Task<AdminResult> RemoveUserFromTeamAsync(ProjectItem project, string team, string username)
        => Run(project, () => _adminService.RemoveUserFromTeamAsync(Context(project), team, username), string.Format(Strings.Admin_UserRemovedFromTeam, username, team));

    public Task<AdminResult> CreateTeamAsync(ProjectItem project, string team, string? description)
        => Run(project, () => _adminService.CreateTeamAsync(Context(project), team, description), string.Format(Strings.Admin_TeamCreated, team));

    public Task<AdminResult> DeleteTeamAsync(ProjectItem project, string team)
        => Run(project, () => _adminService.DeleteTeamAsync(Context(project), team), string.Format(Strings.Admin_TeamDeleted, team));

    // ── plumbing ─────────────────────────────────────────────────────────────────

    private static async Task<AdminResult> Run(ProjectItem project, Func<Task> action, string successMessage)
    {
        try
        {
            await action();
            return AdminResult.Ok(successMessage);
        }
        catch (Exception ex)
        {
            return Fail(ex, project);
        }
    }

    /// <summary>Turns an admin failure into the localized message shown in the UI (the raw output goes to the log).</summary>
    public static AdminResult Fail(Exception ex, ProjectItem project)
    {
        string code = project.Code ?? project.Name;
        if (ex is AvevaAdminException ae)
        {
            if (ae.RawOutput.Length > 0) App.Log($"ADMIN [{code}] {ae.Kind}: {ae.Message}\n{ae.RawOutput}");
            string message = ae.Kind switch
            {
                AdminErrorKind.ConnectionFailed => string.Format(Strings.Admin_ErrConnect, code),
                AdminErrorKind.AccessDenied => string.Format(Strings.Admin_ErrReadOnly, code),
                AdminErrorKind.Timeout => Strings.Admin_ErrTimeout,
                AdminErrorKind.ExecutableMissing => string.Format(Strings.Admin_ErrExeMissing, ae.Message),
                _ => ae.Message,
            };
            return new AdminResult(false, message, ae.Kind);
        }
        App.Log($"ADMIN [{code}] failed: {ex}");
        return new AdminResult(false, ex.Message, AdminErrorKind.MacroError);
    }
}
