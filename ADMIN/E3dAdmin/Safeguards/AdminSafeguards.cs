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
            throw new AvevaAdminException(AdminErrorKind.Validation, $"Parameter '{paramName}' cannot be null or empty.");
        }

        if (value.Length > 32)
        {
            throw new AvevaAdminException(AdminErrorKind.Validation, $"Parameter '{paramName}' is too long (maximum 32 characters).");
        }

        if (!ValidIdRegex.IsMatch(value))
        {
            throw new AvevaAdminException(AdminErrorKind.Validation, $"'{value}' contains invalid characters: only letters, digits, underscores and asterisks are allowed.");
        }
    }

    public static void ValidateSecurity(string? security)
    {
        if (string.IsNullOrWhiteSpace(security))
            return;

        if (!security.Equals("Free", StringComparison.OrdinalIgnoreCase) &&
            !security.Equals("General", StringComparison.OrdinalIgnoreCase))
        {
            throw new AvevaAdminException(AdminErrorKind.Validation, $"Invalid security level '{security}'. Must be 'Free' or 'General'.");
        }
    }

    /// <summary>ADMIN passwords are plain words: no spaces, pipes or quotes (they travel inside |…| in the macro).</summary>
    public static void ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return;
        if (password.Length > 32)
            throw new AvevaAdminException(AdminErrorKind.Validation, "Password is too long (maximum 32 characters).");
        if (password.Any(c => char.IsWhiteSpace(c) || c == '|' || c == '\'' || c == '"' || char.IsControl(c)))
            throw new AvevaAdminException(AdminErrorKind.Validation, "Password must not contain spaces, quotes or the | character.");
    }

    /// <summary>A project needs at least one FREE user, and the account running ADMIN must stay FREE.</summary>
    public static void ValidateDemotion(string targetUserName, List<UserInfo> currentUsers, AdminContext context)
    {
        if (targetUserName.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
            throw new AvevaAdminException(AdminErrorKind.Safeguard, "The SYSTEM user must stay a FREE user.");
        if (targetUserName.Equals(context.AdminUser, StringComparison.OrdinalIgnoreCase))
            throw new AvevaAdminException(AdminErrorKind.Safeguard, $"'{targetUserName}' is the administrator account in use; it cannot demote itself.");

        var target = currentUsers.FirstOrDefault(u => u.Name.Equals(targetUserName, StringComparison.OrdinalIgnoreCase));
        if (target == null)
            throw new AvevaAdminException(AdminErrorKind.NotFound, $"User '{targetUserName}' does not exist in project '{context.Project}'.");
        bool isFree = target.Security.Equals("Free", StringComparison.OrdinalIgnoreCase);
        if (isFree && currentUsers.Count(u => u.Security.Equals("Free", StringComparison.OrdinalIgnoreCase)) <= 1)
            throw new AvevaAdminException(AdminErrorKind.Safeguard,
                $"Cannot change '{targetUserName}' to GENERAL: it is the last FREE user in project '{context.Project}'.");
    }

    public static void ValidateUserDeletion(string targetUserName, List<UserInfo> currentUsers, string project)
    {
        if (targetUserName.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            throw new AvevaAdminException(AdminErrorKind.Safeguard, "The SYSTEM user cannot be deleted.");
        }

        var target = currentUsers.FirstOrDefault(u => u.Name.Equals(targetUserName, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            throw new AvevaAdminException(AdminErrorKind.NotFound, $"User '{targetUserName}' does not exist in project '{project}'.");
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
                throw new AvevaAdminException(AdminErrorKind.Safeguard,
                    $"Cannot delete user '{targetUserName}': it is the last FREE user in project '{project}'. At least one FREE user must remain to administer the project.");
            }
        }
    }
}
