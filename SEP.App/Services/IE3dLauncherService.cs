using System.Threading.Tasks;
using SEP.App.Models;

namespace SEP.App.Services;

public interface IE3dLauncherService
{
    Task<(bool Success, string Message)> SwitchAndLaunchAsync(ProjectItem project, string? module = "Design");
    Task<(bool Success, string Message)> SwitchEnvironmentAsync(ProjectItem project);
    Task<(bool Success, string Message)> LaunchE3dProcessAsync();
}
