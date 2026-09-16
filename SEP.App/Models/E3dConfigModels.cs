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
