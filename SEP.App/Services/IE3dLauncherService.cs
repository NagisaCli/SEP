using System.Threading.Tasks;
using SEP.App.Models;

namespace SEP.App.Services;

public interface IE3dLauncherService
{
    /// <summary>Mode "single": registers the project's evars bat in the local library's custom_evars.bat and starts E3D.</summary>
    Task<(bool Success, string Message)> SwitchAndLaunchAsync(ProjectItem project);

    /// <summary>Mode "library": points projects_dir at the library itself so E3D lists all of its projects, then starts E3D.</summary>
    Task<(bool Success, string Message)> LoadLibraryAndLaunchAsync(LibraryItem library);

    /// <summary>Mode "all": every project of "my projects" goes into the managed block, then E3D starts.</summary>
    Task<(bool Success, string Message)> LoadMyProjectsAndLaunchAsync();

    /// <summary>Rewrites the environment for one project without starting E3D (switch only).</summary>
    Task<(bool Success, string Message)> SwitchAsync(ProjectItem project);

    Task<(bool Success, string Message)> LaunchE3dProcessAsync();
}
