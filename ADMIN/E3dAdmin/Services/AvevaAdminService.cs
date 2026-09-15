using System.Text.RegularExpressions;
using E3dAdmin.Models;
using E3dAdmin.Safeguards;

namespace E3dAdmin.Services;

public class AvevaAdminService
{
    private readonly IAvevaProcessRunner _runner;

    public AvevaAdminService(IAvevaProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task<List<UserInfo>> ListUsersAsync(AdminContext context)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));

        string macro = @"$P === LIST USERS ===
/*U
!users = !!collectAllFor('USER', '', ce)
do !u values !users
  !name = !u.namn
  !sec = !u.security
  !desc = !u.description
  !teams = !u.teamLs
  !tStr = ''
  do !t values !teams
    !tStr = !tStr + ' ' + !t.namn
  enddo
  $P USER_DATA|$!name|$!sec|$!desc|$!tStr
enddo
$P === LIST USERS END ===
FINISH
";

        var result = await _runner.RunMacroAsync(context, macro);
        CheckExecutionErrors(result.StandardOutput, context.Project);

        var users = new List<UserInfo>();
        foreach (var line in result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("USER_DATA|"))
            {
                var parts = line.Split('|');
                if (parts.Length >= 5)
                {
                    var user = new UserInfo
                    {
                        Name = parts[1].Trim(),
                        Security = string.IsNullOrWhiteSpace(parts[2]) ? "General" : parts[2].Trim(),
                        Description = parts[3].Trim()
                    };

                    string rawTeams = parts[4].Trim();
                    if (!string.IsNullOrWhiteSpace(rawTeams))
                    {
                        user.Teams = rawTeams.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                             .Select(t => t.Trim())
                                             .ToList();
                    }

                    users.Add(user);
                }
            }
        }

        return users.OrderBy(u => u.Name).ToList();
    }

    public async Task<List<TeamInfo>> ListTeamsAsync(AdminContext context)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));

        string macro = @"$P === LIST TEAMS ===
/*T
!teams = !!collectAllFor('TEAM', '', ce)
do !t values !teams
  !name = !t.namn
  !users = !t.userLs
  !uStr = ''
  do !u values !users
    !uStr = !uStr + ' ' + !u.namn
  enddo
  $P TEAM_DATA|$!name|$!uStr
enddo
$P === LIST TEAMS END ===
FINISH
";

        var result = await _runner.RunMacroAsync(context, macro);
        CheckExecutionErrors(result.StandardOutput, context.Project);

        var teams = new List<TeamInfo>();
        foreach (var line in result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("TEAM_DATA|"))
            {
                var parts = line.Split('|');
                if (parts.Length >= 3)
                {
                    var team = new TeamInfo
                    {
                        Name = parts[1].Trim()
                    };

                    string rawUsers = parts[2].Trim();
                    if (!string.IsNullOrWhiteSpace(rawUsers))
                    {
                        team.Users = rawUsers.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                             .Select(u => u.Trim())
                                             .ToList();
                    }

                    teams.Add(team);
                }
            }
        }

        return teams.OrderBy(t => t.Name).ToList();
    }

    public async Task AddUserAsync(
        AdminContext context,
        string username,
        string team,
        string? password = null,
        string security = "General",
        string? description = null)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        AdminSafeguards.ValidateIdentifier(username, nameof(username));
        AdminSafeguards.ValidateIdentifier(team, nameof(team));
        AdminSafeguards.ValidateSecurity(security);

        string cleanTeam = team.TrimStart('*', '/');
        string sec = security.Equals("Free", StringComparison.OrdinalIgnoreCase) ? "FREE" : "GENERAL";
        string pwdPart = string.IsNullOrWhiteSpace(password) ? "" : $"/{password}";
        string descCmd = string.IsNullOrWhiteSpace(description) ? "" : $"DESCRIPTION '{description.Replace("'", "''")}'";

        string macro = $@"/*U
CREATE USER |{username}|{pwdPart} {sec}
/{username}
{descCmd}
SAVEWORK
SET TEAM {cleanTeam}
TADD |{username}|
SAVEWORK
$P ADD_USER_SUCCESS|{username}|{cleanTeam}
FINISH
";

        var result = await _runner.RunMacroAsync(context, macro);
        CheckExecutionErrors(result.StandardOutput, context.Project);

        if (!result.StandardOutput.Contains("ADD_USER_SUCCESS|"))
        {
            throw new InvalidOperationException(
                $"Failed to add user '{username}'. AVEVA output:\n{result.StandardOutput}");
        }
    }

    public async Task DeleteUserAsync(AdminContext context, string username, bool force = false)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        AdminSafeguards.ValidateIdentifier(username, nameof(username));

        // Enforce safeguards
        var users = await ListUsersAsync(context);
        AdminSafeguards.ValidateUserDeletion(username, users, context.Project);

        string macro = $@"/*U
/{username}
DELETE USER
SAVEWORK
$P DELETE_USER_SUCCESS|{username}
FINISH
";

        var result = await _runner.RunMacroAsync(context, macro);
        CheckExecutionErrors(result.StandardOutput, context.Project);

        if (!result.StandardOutput.Contains("DELETE_USER_SUCCESS|"))
        {
            throw new InvalidOperationException(
                $"Failed to delete user '{username}'. AVEVA output:\n{result.StandardOutput}");
        }
    }

    public async Task AddUserToTeamAsync(AdminContext context, string team, string username)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        AdminSafeguards.ValidateIdentifier(team, nameof(team));
        AdminSafeguards.ValidateIdentifier(username, nameof(username));

        string cleanTeam = team.TrimStart('*', '/');

        string macro = $@"SET TEAM {cleanTeam}
TADD |{username}|
SAVEWORK
$P ADD_TO_TEAM_SUCCESS|{cleanTeam}|{username}
FINISH
";

        var result = await _runner.RunMacroAsync(context, macro);
        CheckExecutionErrors(result.StandardOutput, context.Project);

        if (!result.StandardOutput.Contains("ADD_TO_TEAM_SUCCESS|"))
        {
            throw new InvalidOperationException(
                $"Failed to add user '{username}' to team '{cleanTeam}'. AVEVA output:\n{result.StandardOutput}");
        }
    }

    private static void CheckExecutionErrors(string output, string project)
    {
        if (output.Contains("Failed to open Project") || output.Contains("(43,259)"))
        {
            throw new InvalidOperationException(
                $"无法连接至工程 '{project}'：工程数据库未找到，或管理员账号/密码不正确。请在【凭证管理】中检查该工程的管理员账号及密码。 (Failed to connect to project '{project}'. Verify project database path and administrator credentials.)");
        }

        if (output.Contains("(41,15)") || output.Contains("Read-only module"))
        {
            throw new UnauthorizedAccessException(
                $"无法修改工程 '{project}'：当前管理账号没有写入权限，或工程处于只读模式。 (Cannot modify project '{project}': The current admin credentials do not have write access, or the project is in read-only mode.)");
        }

        if (output.Contains("(1,3)") || output.Contains("User name already defined"))
        {
            throw new InvalidOperationException("用户名称在当前工程中已存在。(User name is already defined in this project.)");
        }

        if (output.Contains("(2,109)") || output.Contains("Name not found"))
        {
            throw new KeyNotFoundException($"指定的用户名或团队名不存在。(Specified user or team name was not found.) Raw AVEVA output:\n{output}");
        }
    }
}
