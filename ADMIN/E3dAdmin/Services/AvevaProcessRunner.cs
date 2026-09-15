using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using E3dAdmin.Models;

namespace E3dAdmin.Services;

public class AvevaProcessRunner : IAvevaProcessRunner
{
    private static Dictionary<string, string>? _cachedEnvironment;
    private static readonly object _envLock = new();

    public async Task<ProcessRunResult> RunMacroAsync(AdminContext context, string macroContent, int timeoutSeconds = 30)
    {
        string adminDir = ResolveAdminDirectory(context.AdminDirectory);
        string admExe = Path.Combine(adminDir, "adm.exe");

        if (!File.Exists(admExe))
        {
            throw new FileNotFoundException($"Cannot find AVEVA Administration executable at: {admExe}");
        }

        var env = GetOrCreateEnvironment(adminDir);

        // Ensure temp macro file is created
        string tempMacroPath = Path.Combine(Path.GetTempPath(), $"e3d_admin_{Guid.NewGuid():N}.mac");
        await File.WriteAllTextAsync(tempMacroPath, macroContent, Encoding.ASCII);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = admExe,
                WorkingDirectory = adminDir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Arguments for adm.exe
            psi.ArgumentList.Add($"-project={context.Project}");
            psi.ArgumentList.Add($"-username={context.AdminUser}");
            psi.ArgumentList.Add($"-password={context.AdminPassword}");
            psi.ArgumentList.Add("-batch");
            psi.ArgumentList.Add("-tty");
            psi.ArgumentList.Add($"-macro={tempMacroPath}");

            // Populate environment
            foreach (var kvp in env)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderr.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Send FINISH to stdin just in case the macro returns to prompt
            try
            {
                await process.StandardInput.WriteLineAsync("FINISH");
                await process.StandardInput.FlushAsync();
            }
            catch
            {
                // Process might already be closed or closed stdin
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch {}
                throw new TimeoutException($"AVEVA Administration execution timed out after {timeoutSeconds} seconds.");
            }

            return new ProcessRunResult(
                process.ExitCode,
                Sanitize(stdout.ToString(), context.AdminPassword),
                Sanitize(stderr.ToString(), context.AdminPassword)
            );
        }
        finally
        {
            if (File.Exists(tempMacroPath))
            {
                try { File.Delete(tempMacroPath); } catch {}
            }
        }
    }

    public string Sanitize(string input, params string?[] sensitiveTokens)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        string sanitized = Regex.Replace(input, @"-password=[^\s]+", "-password=***", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"/PASS\s+/[^\s]+", "PASS /***", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"CREATE USER\s+\|[^\|]+\|/[^\s]+", m =>
        {
            return Regex.Replace(m.Value, @"/[^\s]+", "/***");
        }, RegexOptions.IgnoreCase);

        foreach (var token in sensitiveTokens)
        {
            if (!string.IsNullOrEmpty(token) && token.Length > 2)
            {
                sanitized = sanitized.Replace(token, "***");
            }
        }

        return sanitized;
    }

    private static string ResolveAdminDirectory(string? explicitDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir) && Directory.Exists(explicitDir))
            return explicitDir;

        string? envDir = Environment.GetEnvironmentVariable("AVEVA_ADMIN_DIR");
        if (!string.IsNullOrWhiteSpace(envDir) && Directory.Exists(envDir))
            return envDir;

        string defaultPath = @"D:\AVEVA_ADMIN";
        if (Directory.Exists(defaultPath))
            return defaultPath;

        throw new DirectoryNotFoundException(
            "Could not locate AVEVA Administration directory. Please specify with --admin-dir or set AVEVA_ADMIN_DIR.");
    }

    private static Dictionary<string, string> GetOrCreateEnvironment(string adminDir)
    {
        lock (_envLock)
        {
            if (_cachedEnvironment != null)
                return _cachedEnvironment;

            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Copy existing system environment
            foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables())
            {
                if (de.Key != null && de.Value != null)
                {
                    env[de.Key.ToString()!] = de.Value.ToString()!;
                }
            }

            // Run evars.bat to acquire AVEVA-specific evars
            string evarsBat = Path.Combine(adminDir, "evars.bat");
            if (File.Exists(evarsBat))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"call \"{evarsBat}\" \"{adminDir}\\\" >nul 2>&1 && set\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();

                    foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0)
                        {
                            string k = line.Substring(0, eq);
                            string v = line.Substring(eq + 1);
                            env[k] = v;
                        }
                    }
                }
            }

            env["AVEVA_PRODUCT"] = "ADMIN";
            _cachedEnvironment = env;
            return _cachedEnvironment;
        }
    }
}
