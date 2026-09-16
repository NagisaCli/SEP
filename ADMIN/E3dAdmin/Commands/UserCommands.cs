using System.Text.Json;
using E3dAdmin.Models;
using E3dAdmin.Services;

namespace E3dAdmin.Commands;

public static class UserCommands
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    public static async Task<int> ExecuteListAsync(AdminContext context, AvevaAdminService service)
    {
        bool isJson = context.OutputFormat == "json";
        try
        {
            var users = await service.ListUsersAsync(context);

            if (isJson)
            {
                var payload = users.Select(u => new
                {
                    name = u.Name,
                    security = u.Security,
                    description = u.Description,
                    teams = u.Teams
                }).ToList();
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, project = context.Project, data = payload }, _jsonOpts));
            }
            else
            {
                Console.WriteLine($"Querying users for project '{context.Project}'...");
                Console.WriteLine();
                Console.WriteLine($"Project '{context.Project}' - Total Users: {users.Count}");
                Console.WriteLine(new string('-', 100));
                Console.WriteLine($"{"Username",-20} {"Security",-12} {"Teams",-40} {"Description"}");
                Console.WriteLine(new string('-', 100));
                foreach (var u in users)
                {
                    string teams = u.Teams.Count > 0 ? string.Join(", ", u.Teams) : "(none)";
                    if (teams.Length > 38) teams = teams.Substring(0, 35) + "...";
                    Console.WriteLine($"{u.Name,-20} {u.Security,-12} {teams,-40} {u.Description}");
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

    public static async Task<int> ExecuteAddAsync(
        AdminContext context,
        AvevaAdminService service,
        string username,
        string team,
        string? password,
        string security,
        string? description)
    {
        bool isJson = context.OutputFormat == "json";
        try
        {
            await service.AddUserAsync(context, username, team, password, security, description);

            if (isJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = true,
                    project = context.Project,
                    message = $"User '{username}' created and assigned to team '{team}'."
                }, _jsonOpts));
            }
            else
            {
                Console.WriteLine($"Adding user '{username}' [{security}] to team '{team}' in project '{context.Project}'...");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"SUCCESS: User '{username}' created and assigned to team '{team}' in project '{context.Project}'.");
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

    public static async Task<int> ExecuteDeleteAsync(
        AdminContext context,
        AvevaAdminService service,
        string username,
        bool force)
    {
        bool isJson = context.OutputFormat == "json";
        try
        {
            await service.DeleteUserAsync(context, username, force);

            if (isJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = true,
                    project = context.Project,
                    message = $"User '{username}' successfully deleted."
                }, _jsonOpts));
            }
            else
            {
                Console.WriteLine($"Deleting user '{username}' from project '{context.Project}'...");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"SUCCESS: User '{username}' successfully deleted from project '{context.Project}'.");
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
    /// <summary>Runs one change and prints the outcome in the selected format (used by the newer verbs).</summary>
    public static async Task<int> ExecuteChangeAsync(AdminContext context, Func<Task> action, string doing, string done)
    {
        bool isJson = context.OutputFormat == "json";
        try
        {
            if (!isJson) Console.WriteLine(doing);
            await action();
            if (isJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, project = context.Project, message = done }, _jsonOpts));
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"SUCCESS: {done}");
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
