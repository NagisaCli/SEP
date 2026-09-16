using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEP.App.Models;

/// <summary>Mirror of e3d_paths.json (also written by the Python tools; unknown keys are preserved on save).</summary>
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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Mirror of e3d_projects.json (unknown keys are preserved on save).</summary>
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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public class AppUserSettings
{
    [JsonPropertyName("auto_start")]
    public bool AutoStart { get; set; }

    [JsonPropertyName("minimize_to_tray")]
    public bool MinimizeToTray { get; set; }

    [JsonPropertyName("theme_mode")]
    public string ThemeMode { get; set; } = "Dark";

    /// <summary>UI language: "auto" (follow Windows), "en" or "zh-CN".</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "auto";
}
