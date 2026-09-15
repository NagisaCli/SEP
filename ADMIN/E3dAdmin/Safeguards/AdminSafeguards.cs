using System.Text.RegularExpressions;
using E3dAdmin.Models;

namespace E3dAdmin.Safeguards;

public static class AdminSafeguards
{
    private static readonly Regex ValidIdRegex = new(@"^[A-Za-z0-9_\*]+$", RegexOptions.Compiled);

    public static void ValidateIdentifier(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Parameter '{paramName}' cannot be null or empty.");
        }

        if (value.Length > 32)
        {
            throw new ArgumentException($"Parameter '{paramName}' is too long (maximum 32 characters).");
        }

        if (!ValidIdRegex.IsMatch(value))
        {
            throw new ArgumentException($"Parameter '{paramName}' ('{value}') contains invalid characters. Only alphanumeric, underscores, and asterisks are permitted.");
        }
    }

    public static void ValidateSecurity(string? security)
    {
        if (string.IsNullOrWhiteSpace(security))
            return;

        if (!security.Equals("Free", StringComparison.OrdinalIgnoreCase) &&
            !security.Equals("General", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid security level '{security}'. Must be 'Free' or 'General'.");
        }
    }

    public static void ValidateUserDeletion(string targetUserName, List<UserInfo> currentUsers, string project)
    {
        if (targetUserName.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Deletion of the SYSTEM user is strictly forbidden.");
        }

        var target = currentUsers.FirstOrDefault(u => u.Name.Equals(targetUserName, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            throw new KeyNotFoundException($"User '{targetUserName}' does not exist in project '{project}'.");
        }

        bool isFree = target.Security.Equals("Free", StringComparison.OrdinalIgnoreCase) ||
                      target.Name.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase);

        if (isFree)
        {
            int freeCount = currentUsers.Count(u =>
                u.Security.Equals("Free", StringComparison.OrdinalIgnoreCase) ||
                u.Name.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase));

            if (freeCount <= 1)
            {
                throw new InvalidOperationException(
                    $"Cannot delete user '{targetUserName}': it is the last FREE user in project '{project}'. At least one FREE user must remain to administer the project.");
            }
        }
    }
}
