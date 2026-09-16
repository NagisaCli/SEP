namespace E3dAdmin.Models;

/// <summary>What went wrong while talking to AVEVA Administration; front ends map the kind to their own wording.</summary>
public enum AdminErrorKind
{
    /// <summary>adm.exe could not open the project: wrong administrator credentials or the project databases were not found.</summary>
    ConnectionFailed,
    /// <summary>The project (or its system database) is read-only for this user.</summary>
    AccessDenied,
    NameAlreadyUsed,
    NotFound,
    /// <summary>The user/team change would leave the project without an administrator (or delete SYSTEM).</summary>
    Safeguard,
    /// <summary>ADMIN reported an error while running the macro; <see cref="Exception.Message"/> carries its text.</summary>
    MacroError,
    Timeout,
    ExecutableMissing,
    Validation,
}

public sealed class AvevaAdminException : Exception
{
    public AdminErrorKind Kind { get; }
    /// <summary>Sanitized adm.exe output (passwords masked) for diagnostics.</summary>
    public string RawOutput { get; }

    public AvevaAdminException(AdminErrorKind kind, string message, string rawOutput = "", Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        RawOutput = rawOutput;
    }
}
