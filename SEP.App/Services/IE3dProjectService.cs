using System.Collections.Generic;
using System.Threading.Tasks;
using SEP.App.Models;

namespace SEP.App.Services;

public interface IE3dProjectService
{
    E3dPathsConfig PathsConfig { get; }
    E3dProjectsConfig ProjectsConfig { get; }

    Task<List<ProjectItem>> LoadAllProjectsAsync(bool forceRescan = false);
    Task<ProjectItem?> InspectProjectAsync(string projectPath);
    Task<bool> ToggleFavoriteAsync(string projectCode);
    Task<bool> SetActiveProjectAsync(string projectCode);
    Task<(bool Success, string Message)> CreateProjectAsync(string code, string name, string rootDir, string? templateDir = null);
    Task<(bool Success, string Message)> DecommissionProjectAsync(string projectPath, string archiveDir, bool doArchive, bool doDelete);
    Task SaveConfigAsync();
    void OpenFolder(string path);
}
