using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SEP.App.Models;

namespace SEP.App.Services;

/// <summary>Fields of a project's metadata to change; null members are left untouched.</summary>
public sealed class ProjectMetaUpdate
{
    public string? DisplayName { get; init; }
    /// <summary>Category id, or "" to clear.</summary>
    public string? CategoryId { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Description { get; init; }
    public string? Notes { get; init; }
    /// <summary>One of <see cref="IProjectCatalog.StatusOptions"/>, or "" to clear.</summary>
    public string? Status { get; init; }
    public string? Owner { get; init; }
}

/// <summary>
/// The app's single source of truth for libraries, discovered projects, "my projects", categories and project
/// metadata — backed by the e3d_projects.json / e3d_paths.json files shared with the Python tool. Item
/// properties change in place; list membership changes are announced through <see cref="Changed"/> (raised on
/// any thread).
/// </summary>
public interface IProjectCatalog
{
    /// <summary>Status tokens as stored (shared with the Python UI): 进行中, 已完成, 暂停, 归档.</summary>
    static readonly IReadOnlyList<string> StatusOptions = new[] { "进行中", "已完成", "暂停", "归档" };

    SepData Data { get; }
    E3dPathsConfig Paths { get; }

    IReadOnlyList<LibraryItem> Libraries { get; }
    IReadOnlyList<ProjectItem> Projects { get; }
    /// <summary>"My projects" in the order they were added.</summary>
    IReadOnlyList<ProjectItem> MyProjects { get; }
    IReadOnlyList<CategoryInfo> Categories { get; }
    /// <summary>Every tag in use, most used first.</summary>
    IReadOnlyList<string> AllTags { get; }
    IReadOnlyList<NotificationRecord> ActiveNotifications { get; }

    /// <summary>Local project library that receives single-project launches (settings.local_projects_dir, else projects_dir).</summary>
    string LocalProjectsDir { get; }

    ProjectItem? ActiveProject { get; }
    bool IsScanning { get; }

    event EventHandler? Changed;
    event EventHandler? ScanStateChanged;

    /// <summary>Rescans every library in parallel; without <paramref name="force"/> a scan younger than a minute is reused.</summary>
    Task RescanAllAsync(bool force = false);
    Task RescanLibraryAsync(string libraryId);
    Task<(bool Success, string Message)> AddLibraryAsync(string path);
    (bool Success, string Message) RemoveLibrary(string libraryId);

    /// <summary>Adds or removes the project from "my projects"; returns the new state.</summary>
    bool ToggleMyProject(ProjectItem project);
    void SetMyProjects(IEnumerable<ProjectItem> projects, bool mine);
    void ClearMyProjects();

    void SetLastLaunched(ProjectItem project, string mode);
    void SetLastLaunchedLibrary(LibraryItem library);
    /// <summary>Mode "all": every project of "my projects" is loaded.</summary>
    void SetLastLaunchedAll();

    // ── metadata ─────────────────────────────────────────────────────────────────

    void UpdateProjectMeta(ProjectItem project, ProjectMetaUpdate update);
    void UpdateProjectsMeta(IEnumerable<ProjectItem> projects, ProjectMetaUpdate update);

    CategoryInfo AddCategory(string name, string? color = null);
    (bool Success, string Message) UpdateCategory(string id, string? name, string? color);
    bool RemoveCategory(string id);

    // ── notifications ────────────────────────────────────────────────────────────

    NotificationRecord AddNotification(string level, string title, string message, string? actionLabel = null, string? actionUrl = null);
    void DismissNotification(string idOrAll);

    /// <summary>Replaces the whole data set (config bundle import) and rebuilds every model.</summary>
    void ImportData(SepData data);

    string? GetPluginDisplayName(string pluginName);
    void SetPluginDisplayName(string pluginName, string? displayName);

    void Save();
}
