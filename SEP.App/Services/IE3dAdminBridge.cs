using System.Collections.Generic;
using System.Threading.Tasks;
using E3dAdmin.Models;
using SEP.App.Models;

namespace SEP.App.Services;

/// <summary>Outcome of an administration call, with a message already localized for the UI.</summary>
public sealed record AdminResult(bool Success, string Message, AdminErrorKind? Error = null)
{
    public static AdminResult Ok(string message) => new(true, message);
}

/// <summary>
/// Users and teams of AVEVA projects through the ADMIN module (adm.exe). Every call resolves the project's
/// administrator credentials from <see cref="AdminCredentialStore"/> (SYSTEM/XXXXXX when none is saved) and
/// passes the project's own evars so projects outside the ADMIN install's registration are found too.
/// </summary>
public interface IE3dAdminBridge
{
    /// <summary>Checks that the given login can open the project as an administrator, without saving it.</summary>
    Task<AdminResult> VerifyCredentialsAsync(ProjectItem project, string adminUser, string adminPassword);

    Task<(List<UserInfo> Users, List<TeamInfo> Teams)> GetUsersAndTeamsAsync(ProjectItem project);

    Task<AdminResult> AddUserAsync(ProjectItem project, string username, string? team, string? password, string security, string? description);
    Task<AdminResult> DeleteUserAsync(ProjectItem project, string username);
    Task<AdminResult> SetPasswordAsync(ProjectItem project, string username, string newPassword);
    Task<AdminResult> SetSecurityAsync(ProjectItem project, string username, string security);
    Task<AdminResult> SetDescriptionAsync(ProjectItem project, string username, string description);

    Task<AdminResult> AddUserToTeamAsync(ProjectItem project, string team, string username);
    Task<AdminResult> RemoveUserFromTeamAsync(ProjectItem project, string team, string username);
    Task<AdminResult> CreateTeamAsync(ProjectItem project, string team, string? description);
    Task<AdminResult> DeleteTeamAsync(ProjectItem project, string team);
}
