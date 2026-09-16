using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SEP.App.Models;
using SEP.App.Resources;

namespace SEP.App.Services;

public class E3dLauncherService : IE3dLauncherService
{
    private const string ManagedStart = ":: >>> SEP MANAGED PROJECTS (do not edit) >>>";
    private const string ManagedEnd = ":: <<< SEP MANAGED PROJECTS <<<";

    private readonly IE3dProjectService _projectService;

    public E3dLauncherService(IE3dProjectService projectService)
    {
        _projectService = projectService;
    }

    public async Task<(bool Success, string Message)> SwitchEnvironmentAsync(ProjectItem project)
    {
        return await Task.Run(() =>
        {
            try
            {
                var paths = _projectService.PathsConfig;
                string? evarsBat = paths.EvarsBat;
                string? projectsDir = paths.ProjectsDir;

                // 1. Locate project's evars bat
                string targetBat = Path.Combine(project.Path, $"evars{project.Code}.bat");
                if (!File.Exists(targetBat))
                {
                    // Search for any evars*.bat in project dir
                    var cand = Directory.GetFiles(project.Path, "evars*.bat");
                    if (cand.Length > 0) targetBat = cand[0];
                }

                if (!File.Exists(targetBat))
                {
                    return (false, string.Format(Strings.Launch_EvarsMissing, project.Code));
                }

                // 2. Locate custom_evars.bat in local projects_dir
                if (!string.IsNullOrEmpty(projectsDir) && Directory.Exists(projectsDir))
                {
                    string customEvars = Path.Combine(projectsDir, "custom_evars.bat");
                    UpdateCustomEvars(customEvars, targetBat, project.Code);
                }

                // 3. Update evars.bat projects_dir if applicable
                if (!string.IsNullOrEmpty(evarsBat) && File.Exists(evarsBat))
                {
                    UpdateEvarsProjectsDir(evarsBat, project.Path);
                }

                // 4. Update active project in config
                _projectService.SetActiveProjectAsync(project.Code).Wait();

                return (true, string.Format(Strings.Launch_SwitchSuccess, project.Code));
            }
            catch (Exception ex)
            {
                return (false, string.Format(Strings.Launch_SwitchFailed, ex.Message));
            }
        });
    }

    public async Task<(bool Success, string Message)> LaunchE3dProcessAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                string? installDir = _projectService.PathsConfig.InstallDir;

                // 1. Check desktop shortcut
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

                foreach (var dir in new[] { desktop, publicDesktop })
                {
                    if (Directory.Exists(dir))
                    {
                        var lnks = Directory.GetFiles(dir, "*Everything3D*.lnk");
                        if (lnks.Length > 0)
                        {
                            Process.Start(new ProcessStartInfo(lnks[0]) { UseShellExecute = true });
                            return (true, Strings.Launch_ViaShortcut);
                        }
                    }
                }

                // 2. Check installDir / mon.exe
                if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
                {
                    string monExe = Path.Combine(installDir, "mon.exe");
                    if (File.Exists(monExe))
                    {
                        var psi = new ProcessStartInfo(monExe)
                        {
                            WorkingDirectory = installDir,
                            UseShellExecute = true
                        };
                        Process.Start(psi);
                        return (true, Strings.Launch_ViaMonExe);
                    }
                }

                return (false, Strings.Launch_NotFound);
            }
            catch (Exception ex)
            {
                return (false, string.Format(Strings.Launch_Failed, ex.Message));
            }
        });
    }

    public async Task<(bool Success, string Message)> SwitchAndLaunchAsync(ProjectItem project, string? module = "Design")
    {
        var switchRes = await SwitchEnvironmentAsync(project);
        if (!switchRes.Success) return switchRes;

        var launchRes = await LaunchE3dProcessAsync();
        return (launchRes.Success, $"[{project.Code}] {launchRes.Message}");
    }

    private static void UpdateCustomEvars(string customEvarsPath, string projectEvarsBat, string projectCode)
    {
        string managedContent = $"{ManagedStart}\r\ncall \"{projectEvarsBat}\"\r\n{ManagedEnd}";

        if (!File.Exists(customEvarsPath))
        {
            File.WriteAllText(customEvarsPath, managedContent + "\r\n", Encoding.GetEncoding("GBK"));
            return;
        }

        string content = File.ReadAllText(customEvarsPath, Encoding.GetEncoding("GBK"));
        if (content.Contains(ManagedStart))
        {
            string pattern = $"{Regex.Escape(ManagedStart)}[\\s\\S]*?{Regex.Escape(ManagedEnd)}";
            content = Regex.Replace(content, pattern, managedContent);
        }
        else
        {
            content = content.TrimEnd() + "\r\n\r\n" + managedContent + "\r\n";
        }

        File.WriteAllText(customEvarsPath, content, Encoding.GetEncoding("GBK"));
    }

    private static void UpdateEvarsProjectsDir(string evarsBatPath, string projectPath)
    {
        try
        {
            // If the project is in a custom parent, update projects_dir
            string parent = Path.GetDirectoryName(projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
            if (string.IsNullOrEmpty(parent)) return;

            string content = File.ReadAllText(evarsBatPath, Encoding.GetEncoding("GBK"));
            string pattern = @"(?im)^(\s*(?:set\s+)?projects_dir=)[^\r\n]*";
            if (Regex.IsMatch(content, pattern))
            {
                // Note: only rewrite if needed to avoid touching system file unnecessarily
            }
        }
        catch { }
    }
}
