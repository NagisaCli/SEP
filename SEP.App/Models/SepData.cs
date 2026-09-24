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
    [JsonPropertyName("categories")] public List<CategoryRecord> Categories { get; set; } = new();
    [JsonPropertyName("project_meta")] public Dictionary<string, ProjectMetaRecord> ProjectMeta { get; set; } = new();
    [JsonPropertyName("plugin_meta")] public Dictionary<string, PluginMetaRecord> PluginMeta { get; set; } = new();
    [JsonPropertyName("libraries")] public List<LibraryRecord> Libraries { get; set; } = new();
    [JsonPropertyName("my_projects")] public List<MyProjectRecord> MyProjects { get; set; } = new();
    [JsonPropertyName("my_project_groups")] public List<MyProjectGroupRecord> MyProjectGroups { get; set; } = new();
    [JsonPropertyName("all_projects_cache")] public List<ProjectRecord> AllProjectsCache { get; set; } = new();
    [JsonPropertyName("notifications")] public List<NotificationRecord> Notifications { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SepSettings
{
    /// <summary>Explicit E3D shortcut; empty means "find it automatically".</summary>
    [JsonPropertyName("e3d_lnk")] public string E3dLnk { get; set; } = string.Empty;
    /// <summary>Local project library whose custom_evars.bat receives single-project launches.</summary>
    [JsonPropertyName("local_projects_dir")] public string LocalProjectsDir { get; set; } = string.Empty;
    [JsonPropertyName("last_mode")] public string LastMode { get; set; } = string.Empty;
    /// <summary>Root folder of PML/.NET plug-ins (settings.plugins_dir in the Python tool); empty = detect.</summary>
    [JsonPropertyName("plugins_dir"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PluginsDir { get; set; }
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
    [JsonPropertyName("group_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? GroupId { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class MyProjectGroupRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("is_expanded")] public bool IsExpanded { get; set; } = true;
    [JsonPropertyName("order")] public int Order { get; set; }
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

/// <summary>A business category projects can be filed under (e3d_store.add_category): id = gen_id("cat", lower(name)).</summary>
public sealed class CategoryRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    /// <summary>#RRGGBB.</summary>
    [JsonPropertyName("color")] public string Color { get; set; } = "#4f8cff";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A device/environment notice (e3d_store.add_device_notification); dismissed ones stay for history.</summary>
public sealed class NotificationRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    /// <summary>"info", "warn", "success" or "error".</summary>
    [JsonPropertyName("level")] public string Level { get; set; } = "info";
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("action_label")] public string? ActionLabel { get; set; }
    [JsonPropertyName("action_url")] public string? ActionUrl { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("updated_at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? UpdatedAt { get; set; }
    [JsonPropertyName("dismissed")] public bool Dismissed { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class PluginMetaRecord
{
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

