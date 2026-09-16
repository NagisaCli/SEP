using System.Text;
using System.Text.RegularExpressions;
using E3dAdmin.Models;
using E3dAdmin.Safeguards;

namespace E3dAdmin.Services;

/// <summary>
/// Users and teams of an AVEVA project, driven through adm.exe macros. The command sequences mirror what
/// AVEVA's own Administration forms send (admuser.pmlfrm / admteam.pmlfrm): NEW USER /NAME, PASS |text|,
/// SECU FREE|GENERAL, DESC |text|, SET TEAM name + TADD/TREM |user|, NEW TEAM /*NAME, DELETE USER/TEAM.
/// ADMIN stops a macro at the first error and reports it as "(n,m) text", so every write ends with a
/// SEP_OK marker and the output is checked for both.
/// </summary>
public class AvevaAdminService
{
    private const string OkMarker = "SEP_OK";
    private static readonly Regex ErrorLineRe = new(@"^\s*\((\d+),(\d+)\)\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex TextForbidden = new(@"[|\r\n\0]", RegexOptions.Compiled);

    private readonly IAvevaProcessRunner _runner;

    public AvevaAdminService(IAvevaProcessRunner runner)
    {
        _runner = runner;
    }

    // ── queries ──────────────────────────────────────────────────────────────────

    /// <summary>Checks that the administrator credentials open the project (a FREE user is required for ADMIN).</summary>
    public async Task VerifyCredentialsAsync(AdminContext context)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        var output = await RunAsync(context, $"$P {OkMarker}\nFINISH\n");
        if (!output.Contains(OkMarker))
            throw new AvevaAdminException(AdminErrorKind.ConnectionFailed, $"Could not open project '{context.Project}' with the given credentials.", output);
    }

    private const string UsersMacro = @"$P === LIST USERS ===
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
";

    private const string TeamsMacro = @"$P === LIST TEAMS ===
/*T
!teams = !!collectAllFor('TEAM', '', ce)
do !t values !teams
  !name = !t.namn
  !desc = !t.description
  !users = !t.userLs
  !uStr = ''
  do !u values !users
    !uStr = !uStr + ' ' + !u.namn
  enddo
  $P TEAM_DATA|$!name|$!uStr|$!desc
enddo
$P === LIST TEAMS END ===
";

    public async Task<List<UserInfo>> ListUsersAsync(AdminContext context)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        var output = await RunAsync(context, UsersMacro + "FINISH\n");
        if (!output.Contains("=== LIST USERS END ==="))
            throw new AvevaAdminException(AdminErrorKind.MacroError, FirstErrorText(output) ?? "ADMIN returned no user list.", output);
        return ParseUsers(output);
    }

    public async Task<List<TeamInfo>> ListTeamsAsync(AdminContext context)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        var output = await RunAsync(context, TeamsMacro + "FINISH\n");
        if (!output.Contains("=== LIST TEAMS END ==="))
            throw new AvevaAdminException(AdminErrorKind.MacroError, FirstErrorText(output) ?? "ADMIN returned no team list.", output);
        return ParseTeams(output);
    }

    /// <summary>Users and teams from a single adm.exe run (each run costs a few seconds of start-up).</summary>
    public async Task<(List<UserInfo> Users, List<TeamInfo> Teams)> ListUsersAndTeamsAsync(AdminContext context)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        var output = await RunAsync(context, UsersMacro + TeamsMacro + "FINISH\n");
        if (!output.Contains("=== LIST TEAMS END ==="))
            throw new AvevaAdminException(AdminErrorKind.MacroError, FirstErrorText(output) ?? "ADMIN returned an incomplete listing.", output);
        return (ParseUsers(output), ParseTeams(output));
    }

    private static List<UserInfo> ParseUsers(string output)
    {
        var users = new List<UserInfo>();
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("USER_DATA|")) continue;
            var parts = line.Split('|');
            if (parts.Length < 5) continue;
            string name = parts[1].Trim();
            if (name.Length == 0 || name.StartsWith('=')) continue;   // unnamed element left behind by an aborted NEW USER

            users.Add(new UserInfo
            {
                Name = name,
                Security = string.IsNullOrWhiteSpace(parts[2]) ? "General" : parts[2].Trim(),
                Description = parts[3].Trim(),
                Teams = parts[4].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList(),
            });
        }
        return users.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<TeamInfo> ParseTeams(string output)
    {
        var teams = new List<TeamInfo>();
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("TEAM_DATA|")) continue;
            var parts = line.Split('|');
            if (parts.Length < 3) continue;
            string name = parts[1].Trim();
            if (name.Length == 0 || name.StartsWith('=')) continue;
            teams.Add(new TeamInfo
            {
                Name = name,
                Users = parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(u => u.Trim()).ToList(),
                Description = parts.Length >= 4 ? parts[3].Trim() : string.Empty,
            });
        }
        return teams.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── users ────────────────────────────────────────────────────────────────────

    public async Task AddUserAsync(
        AdminContext context,
        string username,
        string? team,
        string? password = null,
        string security = "General",
        string? description = null)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        AdminSafeguards.ValidateIdentifier(username, nameof(username));
        AdminSafeguards.ValidateSecurity(security);
        AdminSafeguards.ValidatePassword(password);
        string? cleanTeam = CleanTeam(team);
        if (cleanTeam != null) AdminSafeguards.ValidateIdentifier(cleanTeam, nameof(team));

        var sb = new StringBuilder();
        if (cleanTeam != null)
        {
            // SET TEAM only selects the team that TADD will use; doing it first makes a wrong team name fail before the user exists.
            sb.Append($"SET TEAM {cleanTeam}\n");
            sb.Append("handle any\n  $P SEP_ERR_TEAM_NOT_FOUND\n  return\nendhandle\n");
        }
        sb.Append("/*U\n");
        sb.Append($"NEW USER /{username}\n");
        sb.Append("handle (41,12)\n");
        sb.Append("  DELETE USER\n");            // the failed NEW left an unnamed USER element behind
        sb.Append("  $P SEP_ERR_NAME_USED\n");
        sb.Append("  return\n");
        sb.Append("endhandle\n");
        if (!string.IsNullOrEmpty(password)) sb.Append($"PASS |{password}|\n");
        sb.Append($"SECU {SecurityWord(security)}\n");
        if (!string.IsNullOrWhiteSpace(description)) sb.Append($"DESC |{CleanText(description)}|\n");
        sb.Append("SAVEWORK\n");
        if (cleanTeam != null)
        {
            sb.Append($"TADD |{username}|\n");
            sb.Append("SAVEWORK\n");
        }
        sb.Append($"$P {OkMarker}\nFINISH\n");

        var output = await RunAsync(context, sb.ToString());
        ExpectOk(output, new Dictionary<string, (AdminErrorKind, string)>
        {
            ["SEP_ERR_NAME_USED"] = (AdminErrorKind.NameAlreadyUsed, $"A user named '{username}' already exists in project '{context.Project}'."),
            ["SEP_ERR_TEAM_NOT_FOUND"] = (AdminErrorKind.NotFound, $"Team '{cleanTeam}' was not found in project '{context.Project}'; no user was created."),
        });
    }

    public async Task DeleteUserAsync(AdminContext context, string username, bool force = false)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        AdminSafeguards.ValidateIdentifier(username, nameof(username));

        var users = await ListUsersAsync(context);
        AdminSafeguards.ValidateUserDeletion(username, users, context.Project);

        string macro = NavigateToUser(username) + $"DELETE USER\nSAVEWORK\n$P {OkMarker}\nFINISH\n";
        var output = await RunAsync(context, macro);
        ExpectOk(output, UserNotFound(username, context.Project));
    }

    public async Task SetPasswordAsync(AdminContext context, string username, string newPassword)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        AdminSafeguards.ValidateIdentifier(username, nameof(username));
        AdminSafeguards.ValidatePassword(newPassword);
        if (string.IsNullOrEmpty(newPassword))
            throw new AvevaAdminException(AdminErrorKind.Validation, "The new password must not be empty.");

        string macro = NavigateToUser(username) + $"PASS |{newPassword}|\nSAVEWORK\n$P {OkMarker}\nFINISH\n";
        var output = await RunAsync(context, macro);
        ExpectOk(output, UserNotFound(username, context.Project));
    }

    public async Task SetSecurityAsync(AdminContext context, string username, string security)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        AdminSafeguards.ValidateIdentifier(username, nameof(username));
        AdminSafeguards.ValidateSecurity(security);

        if (!security.Equals("Free", StringComparison.OrdinalIgnoreCase))
        {
            var users = await ListUsersAsync(context);
            AdminSafeguards.ValidateDemotion(username, users, context);
        }

        string macro = NavigateToUser(username) + $"SECU {SecurityWord(security)}\nSAVEWORK\n$P {OkMarker}\nFINISH\n";
        var output = await RunAsync(context, macro);
        ExpectOk(output, UserNotFound(username, context.Project));
    }

    public async Task SetDescriptionAsync(AdminContext context, string username, string description)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        AdminSafeguards.ValidateIdentifier(username, nameof(username));

        string macro = NavigateToUser(username) + $"DESC |{CleanText(description)}|\nSAVEWORK\n$P {OkMarker}\nFINISH\n";
        var output = await RunAsync(context, macro);
        ExpectOk(output, UserNotFound(username, context.Project));
    }

    // ── teams ────────────────────────────────────────────────────────────────────

    public async Task AddUserToTeamAsync(AdminContext context, string team, string username)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        AdminSafeguards.ValidateIdentifier(username, nameof(username));
        string cleanTeam = CleanTeam(team) ?? throw new AvevaAdminException(AdminErrorKind.Validation, "Team name is required.");
        AdminSafeguards.ValidateIdentifier(cleanTeam, nameof(team));

        string macro = $"SET TEAM {cleanTeam}\nhandle any\n  $P SEP_ERR_TEAM_NOT_FOUND\n  return\nendhandle\n" +
                       $"TADD |{username}|\nhandle any\n  $P SEP_ERR_USER_NOT_FOUND\n  return\nendhandle\n" +
                       $"SAVEWORK\n$P {OkMarker}\nFINISH\n";
        var output = await RunAsync(context, macro);
        ExpectOk(output, TeamMemberErrors(cleanTeam, username, context.Project));
    }

    public async Task RemoveUserFromTeamAsync(AdminContext context, string team, string username)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        AdminSafeguards.ValidateIdentifier(username, nameof(username));
        string cleanTeam = CleanTeam(team) ?? throw new AvevaAdminException(AdminErrorKind.Validation, "Team name is required.");
        AdminSafeguards.ValidateIdentifier(cleanTeam, nameof(team));

        string macro = $"SET TEAM {cleanTeam}\nhandle any\n  $P SEP_ERR_TEAM_NOT_FOUND\n  return\nendhandle\n" +
                       $"TREM |{username}|\nhandle any\n  $P SEP_ERR_USER_NOT_FOUND\n  return\nendhandle\n" +
                       $"SAVEWORK\n$P {OkMarker}\nFINISH\n";
        var output = await RunAsync(context, macro);
        ExpectOk(output, TeamMemberErrors(cleanTeam, username, context.Project));
    }

    public async Task CreateTeamAsync(AdminContext context, string team, string? description = null)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        string cleanTeam = CleanTeam(team) ?? throw new AvevaAdminException(AdminErrorKind.Validation, "Team name is required.");
        AdminSafeguards.ValidateIdentifier(cleanTeam, nameof(team));

        var sb = new StringBuilder();
        sb.Append("/*T\n");
        sb.Append($"NEW TEAM /*{cleanTeam}\n");
        sb.Append("handle (41,12)\n  DELETE TEAM\n  $P SEP_ERR_NAME_USED\n  return\nendhandle\n");
        if (!string.IsNullOrWhiteSpace(description)) sb.Append($"DESC |{CleanText(description)}|\n");
        sb.Append($"SAVEWORK\n$P {OkMarker}\nFINISH\n");

        var output = await RunAsync(context, sb.ToString());
        ExpectOk(output, new Dictionary<string, (AdminErrorKind, string)>
        {
            ["SEP_ERR_NAME_USED"] = (AdminErrorKind.NameAlreadyUsed, $"A team named '{cleanTeam}' already exists in project '{context.Project}'."),
        });
    }

    public async Task DeleteTeamAsync(AdminContext context, string team)
    {
        AdminSafeguards.ValidateIdentifier(context.Project, nameof(context.Project));
        string cleanTeam = CleanTeam(team) ?? throw new AvevaAdminException(AdminErrorKind.Validation, "Team name is required.");
        AdminSafeguards.ValidateIdentifier(cleanTeam, nameof(team));
        if (cleanTeam.Equals("MASTER", StringComparison.OrdinalIgnoreCase))
            throw new AvevaAdminException(AdminErrorKind.Safeguard, "The MASTER team cannot be deleted.");

        string macro = $"/*{cleanTeam}\nhandle any\n  $P SEP_ERR_TEAM_NOT_FOUND\n  return\nendhandle\n" +
                       $"DELETE TEAM\nSAVEWORK\n$P {OkMarker}\nFINISH\n";
        var output = await RunAsync(context, macro);
        ExpectOk(output, new Dictionary<string, (AdminErrorKind, string)>
        {
            ["SEP_ERR_TEAM_NOT_FOUND"] = (AdminErrorKind.NotFound, $"Team '{cleanTeam}' was not found in project '{context.Project}'."),
        });
    }

    // ── macro helpers ────────────────────────────────────────────────────────────

    /// <summary>Makes the named user the current element, going through the user world so an MDB of the same name cannot be hit.</summary>
    private static string NavigateToUser(string username) =>
        "/*U\n" +
        $"!sepUsers = !!collectAllFor('USER', 'NAMN eq ''{username}''', ce)\n" +
        "!sepCount = !sepUsers.size()\n" +
        "if (!sepCount eq 0) then\n  $P SEP_ERR_USER_NOT_FOUND\n  return\nendif\n" +
        "!!ce = !sepUsers[1]\n";

    private static Dictionary<string, (AdminErrorKind, string)> UserNotFound(string username, string project) => new()
    {
        ["SEP_ERR_USER_NOT_FOUND"] = (AdminErrorKind.NotFound, $"User '{username}' was not found in project '{project}'."),
    };

    private static Dictionary<string, (AdminErrorKind, string)> TeamMemberErrors(string team, string username, string project) => new()
    {
        ["SEP_ERR_TEAM_NOT_FOUND"] = (AdminErrorKind.NotFound, $"Team '{team}' was not found in project '{project}'."),
        ["SEP_ERR_USER_NOT_FOUND"] = (AdminErrorKind.NotFound, $"User '{username}' was not found in project '{project}'."),
    };

    private static string SecurityWord(string security) =>
        security.Equals("Free", StringComparison.OrdinalIgnoreCase) ? "FREE" : "GENERAL";

    private static string? CleanTeam(string? team)
    {
        if (string.IsNullOrWhiteSpace(team)) return null;
        string t = team.Trim().TrimStart('*', '/').Trim();
        return t.Length == 0 ? null : t;
    }

    private static string CleanText(string text)
    {
        string t = TextForbidden.Replace(text, " ").Trim();
        return t.Length > 120 ? t[..120] : t;
    }

    private async Task<string> RunAsync(AdminContext context, string macro)
    {
        ProcessRunResult result;
        try
        {
            result = await _runner.RunMacroAsync(context, macro);
        }
        catch (AvevaAdminException) { throw; }
        catch (Exception ex)
        {
            throw new AvevaAdminException(AdminErrorKind.MacroError, ex.Message, string.Empty, ex);
        }

        string output = result.StandardOutput;
        if (output.Contains("Failed to open Project", StringComparison.OrdinalIgnoreCase) || output.Contains("(43,259)"))
        {
            throw new AvevaAdminException(AdminErrorKind.ConnectionFailed,
                $"Failed to open project '{context.Project}': the administrator credentials are wrong or the project databases were not found.", output);
        }
        if (output.Contains("(41,15)") || output.Contains("Read-only module", StringComparison.OrdinalIgnoreCase))
        {
            throw new AvevaAdminException(AdminErrorKind.AccessDenied,
                $"Project '{context.Project}' is read-only for '{context.AdminUser}'.", output);
        }
        return output;
    }

    /// <summary>Success needs the SEP_OK marker; otherwise a SEP_ERR marker or the first ADMIN "(n,m)" line explains why.</summary>
    private static void ExpectOk(string output, Dictionary<string, (AdminErrorKind Kind, string Message)> markers)
    {
        if (output.Contains(OkMarker)) return;
        foreach (var kvp in markers)
        {
            if (output.Contains(kvp.Key))
                throw new AvevaAdminException(kvp.Value.Kind, kvp.Value.Message, output);
        }
        string? err = FirstErrorText(output);
        if (err != null && (err.Contains("already used", StringComparison.OrdinalIgnoreCase) || err.Contains("already defined", StringComparison.OrdinalIgnoreCase)))
            throw new AvevaAdminException(AdminErrorKind.NameAlreadyUsed, err, output);
        if (err != null && err.Contains("not found", StringComparison.OrdinalIgnoreCase))
            throw new AvevaAdminException(AdminErrorKind.NotFound, err, output);
        throw new AvevaAdminException(AdminErrorKind.MacroError, err ?? "ADMIN did not confirm the change.", output);
    }

    private static string? FirstErrorText(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var m = ErrorLineRe.Match(line.TrimEnd('\r'));
            if (m.Success)
            {
                string text = m.Groups[3].Value.Trim();
                return text.Length > 0 ? $"({m.Groups[1].Value},{m.Groups[2].Value}) {text}" : m.Value.Trim();
            }
        }
        return null;
    }
}
