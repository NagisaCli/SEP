using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SEP.App.Models;
using SEP.App.Resources;

namespace SEP.App.Services;

public class E3dDiagService : IE3dDiagService
{
    private readonly IE3dProjectService _projectService;

    public E3dDiagService(IE3dProjectService projectService)
    {
        _projectService = projectService;
    }

    public async Task<List<SessionLockItem>> ScanAllLocksAsync(IEnumerable<ProjectItem> projects)
    {
        return await Task.Run(() =>
        {
            var results = new List<SessionLockItem>();

            foreach (var proj in projects)
            {
                // The project scan already probed reachability; re-touching an offline UNC path here
                // would block for the full SMB timeout and stall the whole health check.
                if (!proj.Exists || !Directory.Exists(proj.Path)) continue;

                try
                {
                    // Scan *000 subdirectories
                    var zeroDirs = Directory.GetDirectories(proj.Path, "*000", SearchOption.TopDirectoryOnly);
                    foreach (var zd in zeroDirs)
                    {
                        var lcks = Directory.GetFiles(zd, "*.lck", SearchOption.TopDirectoryOnly);
                        foreach (var lck in lcks)
                        {
                            var fi = new FileInfo(lck);
                            results.Add(new SessionLockItem
                            {
                                ProjectCode = proj.Code,
                                FileName = fi.Name,
                                FilePath = fi.FullName,
                                LockTime = fi.LastWriteTime,
                                FileSizeBytes = fi.Length,
                                IsOrphan = true,
                                StatusMessage = Strings.Lock_StatusActive
                            });
                        }
                    }
                }
                catch { }
            }

            return results.OrderByDescending(x => x.LockTime).ToList();
        });
    }

    public async Task<(bool Success, string Message)> UnlockSessionAsync(SessionLockItem item)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(item.FilePath))
                    return (true, Strings.Unlock_AlreadyCleared);

                File.Delete(item.FilePath);
                return (true, string.Format(Strings.Unlock_Success, item.FileName));
            }
            catch (Exception ex)
            {
                return (false, string.Format(Strings.Unlock_Failed, ex.Message));
            }
        });
    }

    public async Task<(int UnlockedCount, int FailedCount)> UnlockAllSessionsAsync(IEnumerable<SessionLockItem> items)
    {
        int ok = 0, fail = 0;
        foreach (var item in items)
        {
            var res = await UnlockSessionAsync(item);
            if (res.Success) ok++; else fail++;
        }
        return (ok, fail);
    }

    public async Task<List<SystemDiagItem>> RunSystemDiagnosticsAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<SystemDiagItem>();
            var paths = _projectService.PathsConfig;

            // 1. E3D Main Installation
            if (!string.IsNullOrEmpty(paths.InstallDir) && Directory.Exists(paths.InstallDir))
            {
                list.Add(new SystemDiagItem
                {
                    Title = Strings.Diag_InstallDirTitle,
                    Category = Strings.Diag_CategoryCore,
                    Detail = string.Format(Strings.Diag_InstallDirFound, paths.InstallDir, paths.E3dVersion ?? "Everything3D"),
                    Severity = DiagSeverity.Success
                });
            }
            else
            {
                list.Add(new SystemDiagItem
                {
                    Title = Strings.Diag_InstallDirTitle,
                    Category = Strings.Diag_CategoryCore,
                    Detail = Strings.Diag_InstallDirMissing,
                    Severity = DiagSeverity.Warning,
                    CanAutoFix = false
                });
            }

            // 2. evars.bat
            if (!string.IsNullOrEmpty(paths.EvarsBat) && File.Exists(paths.EvarsBat))
            {
                list.Add(new SystemDiagItem
                {
                    Title = Strings.Diag_EvarsTitle,
                    Category = Strings.Diag_CategoryIntegration,
                    Detail = string.Format(Strings.Diag_EvarsFound, paths.EvarsBat),
                    Severity = DiagSeverity.Success
                });
            }
            else
            {
                list.Add(new SystemDiagItem
                {
                    Title = Strings.Diag_EvarsTitle,
                    Category = Strings.Diag_CategoryIntegration,
                    Detail = Strings.Diag_EvarsMissing,
                    Severity = DiagSeverity.Warning
                });
            }

            // 3. Projects Dir & custom_evars.bat
            if (!string.IsNullOrEmpty(paths.ProjectsDir) && Directory.Exists(paths.ProjectsDir))
            {
                string customEvars = Path.Combine(paths.ProjectsDir, "custom_evars.bat");
                bool hasCustom = File.Exists(customEvars);

                list.Add(new SystemDiagItem
                {
                    Title = Strings.Diag_ProjectsDirTitle,
                    Category = Strings.Diag_CategoryStorage,
                    Detail = string.Format(Strings.Diag_ProjectsDirFound, paths.ProjectsDir,
                        hasCustom ? Strings.Diag_CustomEvarsPresent : Strings.Diag_CustomEvarsPending),
                    Severity = DiagSeverity.Success
                });
            }
            else
            {
                list.Add(new SystemDiagItem
                {
                    Title = Strings.Diag_ProjectsDirTitle,
                    Category = Strings.Diag_CategoryStorage,
                    Detail = Strings.Diag_ProjectsDirMissing,
                    Severity = DiagSeverity.Error
                });
            }

            // 4. Windows 11 Desktop Runtime
            list.Add(new SystemDiagItem
            {
                Title = Strings.Diag_RuntimeTitle,
                Category = Strings.Diag_CategoryGui,
                Detail = Strings.Diag_RuntimeDetail,
                Severity = DiagSeverity.Success
            });

            return list;
        });
    }
}
