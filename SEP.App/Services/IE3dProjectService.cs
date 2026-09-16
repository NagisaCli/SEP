using System.Threading.Tasks;
using SEP.App.Models;

namespace SEP.App.Services;

/// <summary>Creating and decommissioning project folders (the catalog owns discovery and configuration).</summary>
public interface IE3dProjectService
{
    Task<(bool Success, string Message)> CreateProjectAsync(string code, string name, string rootDir, string? templateDir = null);
    Task<(bool Success, string Message)> DecommissionProjectAsync(ProjectItem project, string archiveDir, bool doArchive, bool doDelete);
    void OpenFolder(string path);
}
