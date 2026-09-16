using System;
using E3dAdmin.Commands;
using E3dAdmin.Models;
using E3dAdmin.Services;

namespace E3dAdmin;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "help")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            bool jsonMode = args.Any(a => a.Equals("--format", StringComparison.OrdinalIgnoreCase));
            if (jsonMode)
            {
                // Safe: output JSON so Python always gets parseable stdout
                string errJson = System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = ex.Message });
                Console.WriteLine(errJson);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                Console.ResetColor();

                if (args.Contains("--verbose") || args.Contains("-v"))
                {
                    Console.Error.WriteLine(ex.StackTrace);
                }
            }

            return 1;
        }

    }

    private static async Task<int> RunAsync(string[] args)
    {
        var context = ParseGlobalOptions(args, out var remainingArgs);
        var runner = new AvevaProcessRunner();
        var service = new AvevaAdminService(runner);

        if (remainingArgs.Count < 2)
        {
            PrintUsage();
            return 1;
        }

        string subject = remainingArgs[0].ToLowerInvariant();
        string action = remainingArgs[1].ToLowerInvariant();

        switch (subject)
        {
            case "user":
                return await HandleUserCommandAsync(action, remainingArgs.Skip(2).ToList(), context, service);

            case "team":
                return await HandleTeamCommandAsync(action, remainingArgs.Skip(2).ToList(), context, service);

            default:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Unknown category '{subject}'. Expected 'user' or 'team'.");
                Console.ResetColor();
                PrintUsage();
                return 1;
        }
    }

    private static async Task<int> HandleUserCommandAsync(
        string action,
        List<string> args,
        AdminContext context,
        AvevaAdminService service)
    {
        switch (action)
        {
            case "list":
            {
                if (args.Count < 1)
                {
                    Console.Error.WriteLine("Usage: e3d-admin user list <project>");
                    return 1;
                }
                context.Project = args[0].ToUpperInvariant();
                return await UserCommands.ExecuteListAsync(context, service);
            }

            case "add":
            {
                if (args.Count < 2)
                {
                    Console.Error.WriteLine("Usage: e3d-admin user add <project> <user> --team <team> [--password <pwd>] [--security <Free|General>] [--desc <text>]");
                    return 1;
                }

                context.Project = args[0].ToUpperInvariant();
                string username = args[1].ToUpperInvariant();

                string? team = null;
                string? password = null;
                string security = "General";
                string? description = null;

                for (int i = 2; i < args.Count; i++)
                {
                    if (args[i].Equals("--team", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                    {
                        team = args[++i];
                    }
                    else if (args[i].Equals("--password", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                    {
                        password = args[++i];
                    }
                    else if (args[i].Equals("--security", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                    {
                        security = args[++i];
                    }
                    else if (args[i].Equals("--desc", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                    {
                        description = args[++i];
                    }
                }

                if (string.IsNullOrWhiteSpace(team))
                {
                    Console.Error.WriteLine("ERROR: User must be assigned to an initial team using '--team <team>'.");
                    return 1;
                }

                return await UserCommands.ExecuteAddAsync(context, service, username, team, password, security, description);
            }

            case "delete":
            {
                if (args.Count < 2)
                {
                    Console.Error.WriteLine("Usage: e3d-admin user delete <project> <user> [--force]");
                    return 1;
                }

                context.Project = args[0].ToUpperInvariant();
                string username = args[1].ToUpperInvariant();
                bool force = args.Contains("--force");

                return await UserCommands.ExecuteDeleteAsync(context, service, username, force);
            }

            case "verify":
            {
                if (args.Count < 1)
                {
                    Console.Error.WriteLine("Usage: e3d-admin user verify <project>");
                    return 1;
                }
                context.Project = args[0].ToUpperInvariant();
                return await UserCommands.ExecuteChangeAsync(context, () => service.VerifyCredentialsAsync(context),
                    $"Checking administrator access to project '{context.Project}'...",
                    $"'{context.AdminUser}' can administer project '{context.Project}'.");
            }

            case "password":
            {
                if (args.Count < 3)
                {
                    Console.Error.WriteLine("Usage: e3d-admin user password <project> <user> <new-password>");
                    return 1;
                }
                context.Project = args[0].ToUpperInvariant();
                string username = args[1].ToUpperInvariant();
                return await UserCommands.ExecuteChangeAsync(context, () => service.SetPasswordAsync(context, username, args[2]),
                    $"Changing the password of '{username}' in project '{context.Project}'...",
                    $"Password of '{username}' changed.");
            }

            case "security":
            {
                if (args.Count < 3)
                {
                    Console.Error.WriteLine("Usage: e3d-admin user security <project> <user> <Free|General>");
                    return 1;
                }
                context.Project = args[0].ToUpperInvariant();
                string username = args[1].ToUpperInvariant();
                return await UserCommands.ExecuteChangeAsync(context, () => service.SetSecurityAsync(context, username, args[2]),
                    $"Setting '{username}' to {args[2]} in project '{context.Project}'...",
                    $"'{username}' is now a {args[2].ToUpperInvariant()} user.");
            }

            case "describe":
            {
                if (args.Count < 3)
                {
                    Console.Error.WriteLine("Usage: e3d-admin user describe <project> <user> <text>");
                    return 1;
                }
                context.Project = args[0].ToUpperInvariant();
                string username = args[1].ToUpperInvariant();
                string text = string.Join(' ', args.Skip(2));
                return await UserCommands.ExecuteChangeAsync(context, () => service.SetDescriptionAsync(context, username, text),
                    $"Updating the description of '{username}'...",
                    $"Description of '{username}' updated.");
            }

            default:
                Console.Error.WriteLine($"Unknown user action '{action}'. Valid actions: list, add, delete, verify, password, security, describe");
                return 1;
        }
    }

    private static async Task<int> HandleTeamCommandAsync(
        string action,
        List<string> args,
        AdminContext context,
        AvevaAdminService service)
    {
        switch (action)
        {
            case "list":
            {
                if (args.Count < 1)
                {
                    Console.Error.WriteLine("Usage: e3d-admin team list <project>");
                    return 1;
                }
                context.Project = args[0].ToUpperInvariant();
                return await TeamCommands.ExecuteListAsync(context, service);
            }

            case "add-user":
            {
                if (args.Count < 3)
                {
                    Console.Error.WriteLine("Usage: e3d-admin team add-user <project> <team> <user>");
                    return 1;
                }

                context.Project = args[0].ToUpperInvariant();
                string team = args[1].ToUpperInvariant();
                string username = args[2].ToUpperInvariant();

                return await TeamCommands.ExecuteAddUserAsync(context, service, team, username);
            }

            case "remove-user":
            {
                if (args.Count < 3)
                {
                    Console.Error.WriteLine("Usage: e3d-admin team remove-user <project> <team> <user>");
                    return 1;
                }
                context.Project = args[0].ToUpperInvariant();
                string team = args[1].ToUpperInvariant();
                string username = args[2].ToUpperInvariant();
                return await UserCommands.ExecuteChangeAsync(context, () => service.RemoveUserFromTeamAsync(context, team, username),
                    $"Removing '{username}' from team '{team}'...",
                    $"'{username}' removed from team '{team}'.");
            }

            case "add":
            {
                if (args.Count < 2)
                {
                    Console.Error.WriteLine("Usage: e3d-admin team add <project> <team> [--desc <text>]");
                    return 1;
                }
                context.Project = args[0].ToUpperInvariant();
                string team = args[1].ToUpperInvariant();
                string? desc = null;
                for (int i = 2; i < args.Count; i++)
                {
                    if (args[i].Equals("--desc", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) desc = args[++i];
                }
                return await UserCommands.ExecuteChangeAsync(context, () => service.CreateTeamAsync(context, team, desc),
                    $"Creating team '{team}' in project '{context.Project}'...",
                    $"Team '{team}' created.");
            }

            case "delete":
            {
                if (args.Count < 2)
                {
                    Console.Error.WriteLine("Usage: e3d-admin team delete <project> <team>");
                    return 1;
                }
                context.Project = args[0].ToUpperInvariant();
                string team = args[1].ToUpperInvariant();
                return await UserCommands.ExecuteChangeAsync(context, () => service.DeleteTeamAsync(context, team),
                    $"Deleting team '{team}' from project '{context.Project}'...",
                    $"Team '{team}' deleted.");
            }

            default:
                Console.Error.WriteLine($"Unknown team action '{action}'. Valid actions: list, add, delete, add-user, remove-user");
                return 1;
        }
    }

    private static AdminContext ParseGlobalOptions(string[] args, out List<string> remaining)
    {
        var context = new AdminContext
        {
            AdminUser = Environment.GetEnvironmentVariable("E3D_ADMIN_USER") ?? "SYSTEM",
            AdminPassword = Environment.GetEnvironmentVariable("E3D_ADMIN_PASSWORD") ?? "XXXXXX",
            AdminDirectory = Environment.GetEnvironmentVariable("AVEVA_ADMIN_DIR")
        };

        remaining = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg.Equals("--admin-user", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                context.AdminUser = args[++i];
            }
            else if (arg.Equals("--admin-pass", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                context.AdminPassword = args[++i];
            }
            else if (arg.Equals("--admin-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                context.AdminDirectory = args[++i];
            }
            else if (arg.Equals("--format", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                context.OutputFormat = args[++i].ToLowerInvariant();
            }
            else if (arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase) || arg.Equals("-v"))
            {
                context.Verbose = true;
            }
            else
            {
                remaining.Add(arg);
            }
        }

        return context;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"
AVEVA E3D / Administration 2.1 Headless CLI (e3d-admin)
=====================================================
Manage project Users and Teams headlessly without opening the GUI.

USAGE:
  e3d-admin user list <project>
  e3d-admin user add <project> <user> --team <team> [--password <pwd>] [--security <Free|General>] [--desc <text>]
  e3d-admin user delete <project> <user> [--force]
  e3d-admin user verify <project>
  e3d-admin user password <project> <user> <new-password>
  e3d-admin user security <project> <user> <Free|General>
  e3d-admin user describe <project> <user> <text>
  e3d-admin team list <project>
  e3d-admin team add <project> <team> [--desc <text>]
  e3d-admin team delete <project> <team>
  e3d-admin team add-user <project> <team> <user>
  e3d-admin team remove-user <project> <team> <user>

GLOBAL OPTIONS:
  --admin-user <user>   Administrator login name (default: env E3D_ADMIN_USER or SYSTEM)
  --admin-pass <pass>   Administrator password (default: env E3D_ADMIN_PASSWORD or XXXXXX)
  --admin-dir  <path>   AVEVA Administration install path (default: env AVEVA_ADMIN_DIR or D:\AVEVA_ADMIN)
  --format <text|json>  Output format: 'text' (default, human-readable table) or 'json' (machine-readable)
  -v, --verbose         Display verbose diagnostic output
  -h, --help            Show this help information

EXAMPLES:
  e3d-admin user list APS
  e3d-admin user list APS --format json
  e3d-admin user add APS JOHN --team *MASTER --security Free --desc ""Lead Engineer""
  e3d-admin team add-user APS *PIPING JOHN
  e3d-admin user delete APS JOHN
");
    }
}
