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

    /// <summary>
    /// Extra environment variables for adm.exe, typically the project's own evars (XXX000=…, XXXDFLTS=…):
    /// projects that are not registered in the ADMIN install's evars/custom_evars are only found this way.
    /// </summary>
    public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-call deadline; adm.exe needs ~3 s to start, list macros a few seconds more.</summary>
    public int TimeoutSeconds { get; set; } = 40;
}
