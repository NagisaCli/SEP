using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEP.App.Models;

/// <summary>
/// e3d_projects.json, schema version 3 — the same file the Python tool (e3d_store.py) reads and writes, so
/// libraries, discovered projects and "my projects" are shared between both front ends. Every class keeps
/// unknown keys in <c>Extra</c>, so fields only one side knows about survive a round trip.
/// </summary>
public sealed class SepData
{
    [JsonPropertyName("version")] public int Version { get; set; } = 3;
    [JsonPropertyName("settings")] public SepSettings Settings { get; set; } = new();
    [JsonPropertyName("categories")] public List<JsonElement> Categories { get; set; } = new();
    [JsonPropertyName("project_meta")] public Dictionary<string, ProjectMetaRecord> ProjectMeta { get; set; } = new();
    [JsonPropertyName("libraries")] public List<LibraryRecord> Libraries { get; set; } = new();
    [JsonPropertyName("my_projects")] public List<MyProjectRecord> MyProjects { get; set; } = new();
    [JsonPropertyName("all_projects_cache")] public List<ProjectRecord> AllProjectsCache { get; set; } = new();
    [JsonPropertyName("notifications")] public List<JsonElement> Notifications { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SepSettings
{
    /// <summary>Explicit E3D shortcut; empty means "find it automatically".</summary>
    [JsonPropertyName("e3d_lnk")] public string E3dLnk { get; set; } = string.Empty;
    /// <summary>Local project library whose custom_evars.bat receives single-project launches.</summary>
    [JsonPropertyName("local_projects_dir")] public string LocalProjectsDir { get; set; } = string.Empty;
    [JsonPropertyName("last_mode")] public string LastMode { get; set; } = string.Empty;
    /// <summary>Name of the project launched last (what the Python UI calls the active project).</summary>
    [JsonPropertyName("last_launched")] public string LastLaunched { get; set; } = string.Empty;
    // Keys below are only used by this client.
    [JsonPropertyName("language")] public string Language { get; set; } = "auto";
    /// <summary>"auto" (follow Windows), "dark" or "light".</summary>
    [JsonPropertyName("theme")] public string Theme { get; set; } = "auto";
    [JsonPropertyName("auto_start")] public bool AutoStart { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LibraryRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    /// <summary>"collection" (folder of project folders) or "project" (a single project folder / evars file).</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "collection";
    /// <summary>"local" or "unc".</summary>
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "local";
    [JsonPropertyName("source")] public string Source { get; set; } = "user";
    [JsonPropertyName("last_scan")] public string? LastScan { get; set; }
    [JsonPropertyName("last_error")] public string? LastError { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ProjectRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    /// <summary>The XXX of evarsXXX.bat.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("bat_path")] public string BatPath { get; set; } = string.Empty;
    [JsonPropertyName("lib_path")] public string LibPath { get; set; } = string.Empty;
    /// <summary>Folder of the project when it lives in a sub-folder of the library; null for a bat in the library root.</summary>
    [JsonPropertyName("project_dir")] public string? ProjectDir { get; set; }
    [JsonPropertyName("lib_id")] public string? LibId { get; set; }
    [JsonPropertyName("discovered_at")] public string? DiscoveredAt { get; set; }
    /// <summary>AVEVA project code (the XXX of the "set XXX000=" line), read from the evars file; this client only.</summary>
    [JsonPropertyName("code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Code { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MyProjectRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("bat_path")] public string BatPath { get; set; } = string.Empty;
    [JsonPropertyName("lib_id")] public string? LibId { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "user";
    [JsonPropertyName("added_at")] public string? AddedAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// Per-project metadata maintained by the Python UI (e3d_store._clean_meta: name, category_id, tags, description,
/// notes, status, owner, updated_at); read here for display. Absent fields are not written back as null.
/// </summary>
public sealed class ProjectMetaRecord
{
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
    [JsonPropertyName("category_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CategoryId { get; set; }
    [JsonPropertyName("tags"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Tags { get; set; }
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Description { get; set; }
    [JsonPropertyName("notes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Notes { get; set; }
    [JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Status { get; set; }
    [JsonPropertyName("owner"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Owner { get; set; }
    [JsonPropertyName("updated_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? UpdatedAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
