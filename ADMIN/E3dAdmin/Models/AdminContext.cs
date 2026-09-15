namespace E3dAdmin.Models;

public class AdminContext
{
    public string Project { get; set; } = string.Empty;
    public string AdminUser { get; set; } = "SYSTEM";
    public string AdminPassword { get; set; } = "XXXXXX";
    public string? AdminDirectory { get; set; }
    public bool Verbose { get; set; }
    /// <summary>Output format: "text" (default) or "json" for machine-readable output.</summary>
    public string OutputFormat { get; set; } = "text";
}
