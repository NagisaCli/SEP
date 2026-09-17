using System.Collections.Generic;

namespace SEP.App.Models;

public enum CheckStatus
{
    Ok,
    Warn,
    Fail,
    Skip,
}

/// <summary>A repair the tools page can run or at least explain (steps + commands), like e3d_diag's "fix" objects.</summary>
public sealed record DiagFix(string Id, string Title, IReadOnlyList<string> Steps, IReadOnlyList<string> Commands, bool RequiresAdmin, string? Path = null);

/// <summary>One line of a configuration file that a check flagged.</summary>
public sealed record FlaggedLine(int LineNumber, string RawLine, string Path, string Reason, bool InManagedBlock);

public sealed class DiagCheck
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public CheckStatus Status { get; init; }
    public string Detail { get; init; } = string.Empty;
    public DiagFix? Fix { get; init; }
    public IReadOnlyList<FlaggedLine> Lines { get; init; } = new List<FlaggedLine>();
    public bool HasLines => Lines.Count > 0;
    public bool HasFix => Fix != null;
}

public sealed class DiagReport
{
    public bool Ok { get; init; }
    public string Path { get; init; } = string.Empty;
    public string Protocol { get; init; } = "local";
    public IReadOnlyList<DiagCheck> Checks { get; init; } = new List<DiagCheck>();
    public IReadOnlyList<DiagFix> Fixes { get; init; } = new List<DiagFix>();
    public int Problems { get; init; }
}

/// <summary>Outcome of a tool run: a headline plus the individual changes it made.</summary>
public sealed record ToolResult(bool Ok, string Message, IReadOnlyList<string> Output, bool NeedsAdmin = false)
{
    public static ToolResult Success(string message, IEnumerable<string>? output = null) => new(true, message, output?.ToList() ?? new List<string>());
    public static ToolResult Failure(string message, IEnumerable<string>? output = null, bool needsAdmin = false) => new(false, message, output?.ToList() ?? new List<string>(), needsAdmin);
}
