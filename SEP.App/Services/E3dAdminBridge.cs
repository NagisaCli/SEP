using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using E3dAdmin.Models;
using E3dAdmin.Services;

namespace SEP.App.Services;

public class E3dAdminBridge : IE3dAdminBridge
{
    private readonly AvevaAdminService _adminService;

    public E3dAdminBridge()
    {
        var runner = new AvevaProcessRunner();
        _adminService = new AvevaAdminService(runner);
    }

    public async Task<List<UserInfo>> GetUsersAsync(string projectCode, string? adminUser = "SYSTEM", string? adminPass = "XXXXXX")
    {
        var context = new AdminContext
        {
            Project = projectCode.ToUpperInvariant(),
            AdminUser = adminUser ?? "SYSTEM",
            AdminPassword = adminPass ?? "XXXXXX"
        };
        return await _adminService.ListUsersAsync(context);
    }

    public async Task<List<TeamInfo>> GetTeamsAsync(string projectCode, string? adminUser = "SYSTEM", string? adminPass = "XXXXXX")
    {
        var context = new AdminContext
        {
            Project = projectCode.ToUpperInvariant(),
            AdminUser = adminUser ?? "SYSTEM",
            AdminPassword = adminPass ?? "XXXXXX"
        };
        return await _adminService.ListTeamsAsync(context);
    }

    public async Task<(bool Success, string Message)> AddUserAsync(string projectCode, string username, string team, string? password, string security = "General", string? desc = null)
    {
        var context = new AdminContext
        {
            Project = projectCode.ToUpperInvariant(),
            AdminUser = "SYSTEM",
            AdminPassword = "XXXXXX"
        };
        try
        {
            await _adminService.AddUserAsync(context, username, team, password, security, desc);
            return (true, $"用户 [{username}] 成功创建并分配至团队 [{team}]！");
        }
        catch (Exception ex)
        {
            return (false, $"创建用户失败: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> DeleteUserAsync(string projectCode, string username, bool force = false)
    {
        var context = new AdminContext
        {
            Project = projectCode.ToUpperInvariant(),
            AdminUser = "SYSTEM",
            AdminPassword = "XXXXXX"
        };
        try
        {
            await _adminService.DeleteUserAsync(context, username, force);
            return (true, $"用户 [{username}] 已成功删除！");
        }
        catch (Exception ex)
        {
            return (false, $"删除用户失败: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> AddUserToTeamAsync(string projectCode, string team, string username)
    {
        var context = new AdminContext
        {
            Project = projectCode.ToUpperInvariant(),
            AdminUser = "SYSTEM",
            AdminPassword = "XXXXXX"
        };
        try
        {
            await _adminService.AddUserToTeamAsync(context, team, username);
            return (true, $"用户 [{username}] 已加入团队 [{team}]！");
        }
        catch (Exception ex)
        {
            return (false, $"分配团队失败: {ex.Message}");
        }
    }
}
