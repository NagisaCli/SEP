using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SEP.App.Models;

public class E3dPathsConfig
{
    [JsonPropertyName("evars_bat")]
    public string? EvarsBat { get; set; }

    [JsonPropertyName("evars_init")]
    public string? EvarsInit { get; set; }

    [JsonPropertyName("install_dir")]
    public string? InstallDir { get; set; }

    [JsonPropertyName("projects_dir")]
    public string? ProjectsDir { get; set; }

    [JsonPropertyName("e3d_version")]
    public string? E3dVersion { get; set; }
}

public class E3dProjectsConfig
{
    [JsonPropertyName("projects")]
    public Dictionary<string, string> Projects { get; set; } = new();

    [JsonPropertyName("favorites")]
    public List<string> Favorites { get; set; } = new();

    [JsonPropertyName("last_active_project")]
    public string? LastActiveProject { get; set; }

    [JsonPropertyName("settings")]
    public AppUserSettings Settings { get; set; } = new();
}

public class AppUserSettings
{
    [JsonPropertyName("auto_start")]
    public bool AutoStart { get; set; }

    [JsonPropertyName("minimize_to_tray")]
    public bool MinimizeToTray { get; set; }

    [JsonPropertyName("theme_mode")]
    public string ThemeMode { get; set; } = "Dark";
}
