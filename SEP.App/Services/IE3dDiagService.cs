using System.Collections.Generic;
using System.Threading.Tasks;
using SEP.App.Models;

namespace SEP.App.Services;

public interface IE3dDiagService
{
    Task<List<SessionLockItem>> ScanAllLocksAsync(IEnumerable<ProjectItem> projects);
    Task<(bool Success, string Message)> UnlockSessionAsync(SessionLockItem item);
    Task<(int UnlockedCount, int FailedCount)> UnlockAllSessionsAsync(IEnumerable<SessionLockItem> items);
    Task<List<SystemDiagItem>> RunSystemDiagnosticsAsync();
}
