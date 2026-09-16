using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEP.App.Models;
using SEP.App.Resources;

namespace SEP.App.Services;

public class E3dDiagService : IE3dDiagService
{
    private static readonly TimeSpan LockProbeTimeout = TimeSpan.FromSeconds(5);
    private readonly IProjectCatalog _catalog;

    public E3dDiagService(IProjectCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>Lock files of every project in a reachable library, probed in parallel under a deadline each.</summary>
    public async Task<List<SessionLockItem>> ScanAllLocksAsync(IEnumerable<ProjectItem> projects)
    {
        var results = new List<SessionLockItem>();
        var gate = new object();
        using var limiter = new SemaphoreSlim(16);

        await Task.WhenAll(projects.Where(p => !p.IsCached).Select(async proj =>
        {
            await limiter.WaitAsync();
            try
            {
                var work = Task.Run(() => LibraryScanner.FindLockFiles(proj.ProjectDir));
                if (await Task.WhenAny(work, Task.Delay(LockProbeTimeout)) != work) return;
                var locks = await work;
                if (locks == null) return;

                var items = new List<SessionLockItem>();
                foreach (var lck in locks)
                {
                    var fi = new FileInfo(lck);
                    items.Add(new SessionLockItem
                    {
                        ProjectCode = proj.Code ?? proj.Name,
                        FileName = fi.Name,
                        FilePath = fi.FullName,
                        LockTime = fi.LastWriteTime,
                        FileSizeBytes = fi.Length,
                        IsOrphan = true,
                        StatusMessage = Strings.Lock_StatusActive
                    });
                }
                lock (gate) results.AddRange(items);
            }
            catch { }
            finally { limiter.Release(); }
        }));

        return results.OrderByDescending(x => x.LockTime).ToList();
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
            var paths = _catalog.Paths;

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

            // 3. Local project library & custom_evars.bat
            string localDir = _catalog.LocalProjectsDir;
            if (Directory.Exists(localDir))
            {
                bool hasCustom = File.Exists(Path.Combine(localDir, "custom_evars.bat")) || File.Exists(Path.Combine(localDir, "custom_evar.bat"));
                list.Add(new SystemDiagItem
                {
                    Title = Strings.Diag_ProjectsDirTitle,
                    Category = Strings.Diag_CategoryStorage,
                    Detail = string.Format(Strings.Diag_ProjectsDirFound, localDir,
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
