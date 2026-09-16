using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SEP.App.Models;

namespace SEP.App.Services;

/// <summary>
/// The app's single source of truth for libraries, discovered projects and "my projects" — backed by the
/// e3d_projects.json / e3d_paths.json files shared with the Python tool. Item properties change in place;
/// list membership changes are announced through <see cref="Changed"/> (raised on any thread).
/// </summary>
public interface IProjectCatalog
{
    SepData Data { get; }
    E3dPathsConfig Paths { get; }

    IReadOnlyList<LibraryItem> Libraries { get; }
    IReadOnlyList<ProjectItem> Projects { get; }

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
    void SetLastLaunched(ProjectItem project, string mode);
    void SetLastLaunchedLibrary(LibraryItem library);

    void Save();
}
