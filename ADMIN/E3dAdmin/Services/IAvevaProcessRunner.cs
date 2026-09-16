using E3dAdmin.Models;

namespace E3dAdmin.Services;

public record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

public interface IAvevaProcessRunner
{
    Task<ProcessRunResult> RunMacroAsync(AdminContext context, string macroContent, int timeoutSeconds = 0);
    string Sanitize(string input, params string?[] sensitiveTokens);
}
