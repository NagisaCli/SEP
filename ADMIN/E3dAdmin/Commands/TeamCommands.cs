using System.Text.Json;
using E3dAdmin.Models;
using E3dAdmin.Services;

namespace E3dAdmin.Commands;

public static class TeamCommands
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public static async Task<int> ExecuteListAsync(AdminContext context, AvevaAdminService service)
    {
        bool isJson = context.OutputFormat == "json";
        try
        {
            var teams = await service.ListTeamsAsync(context);

            if (isJson)
            {
                var payload = teams.Select(t => new
                {
                    name = t.Name,
                    member_count = t.Users.Count,
                    users = t.Users,
                    description = t.Description
                }).ToList();
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, project = context.Project, data = payload }, _jsonOpts));
            }
            else
            {
                Console.WriteLine($"Querying teams for project '{context.Project}'...");
                Console.WriteLine();
                Console.WriteLine($"Project '{context.Project}' - Total Teams: {teams.Count}");
                Console.WriteLine(new string('-', 100));
                Console.WriteLine($"{"Team Name",-25} {"Member Count",-15} {"Members"}");
                Console.WriteLine(new string('-', 100));
                foreach (var t in teams)
                {
                    string members = t.Users.Count > 0 ? string.Join(", ", t.Users) : "(none)";
                    if (members.Length > 55) members = members.Substring(0, 52) + "...";
                    Console.WriteLine($"{t.Name,-25} {t.Users.Count,-15} {members}");
                }
                Console.WriteLine(new string('-', 100));
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (isJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, _jsonOpts));
                return 1;
            }
            throw;
        }
    }

    public static async Task<int> ExecuteAddUserAsync(
        AdminContext context,
        AvevaAdminService service,
        string team,
        string username)
    {
        bool isJson = context.OutputFormat == "json";
        try
        {
            await service.AddUserToTeamAsync(context, team, username);

            if (isJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = true,
                    project = context.Project,
                    message = $"User '{username}' added to team '{team}'."
                }, _jsonOpts));
            }
            else
            {
                Console.WriteLine($"Adding user '{username}' to team '{team}' in project '{context.Project}'...");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"SUCCESS: User '{username}' added to team '{team}' in project '{context.Project}'.");
                Console.ResetColor();
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (isJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, _jsonOpts));
                return 1;
            }
            throw;
        }
    }
}
