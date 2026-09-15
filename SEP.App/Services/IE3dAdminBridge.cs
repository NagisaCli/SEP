using System.Collections.Generic;
using System.Threading.Tasks;
using E3dAdmin.Models;

namespace SEP.App.Services;

public interface IE3dAdminBridge
{
    Task<List<UserInfo>> GetUsersAsync(string projectCode, string? adminUser = "SYSTEM", string? adminPass = "XXXXXX");
    Task<List<TeamInfo>> GetTeamsAsync(string projectCode, string? adminUser = "SYSTEM", string? adminPass = "XXXXXX");
    Task<(bool Success, string Message)> AddUserAsync(string projectCode, string username, string team, string? password, string security = "General", string? desc = null);
    Task<(bool Success, string Message)> DeleteUserAsync(string projectCode, string username, bool force = false);
    Task<(bool Success, string Message)> AddUserToTeamAsync(string projectCode, string team, string username);
}
