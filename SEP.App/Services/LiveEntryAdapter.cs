using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SEP.App.Services;

/// <summary>SEP-owned connection settings only; E3D runtimes and project scopes remain in e3d_live.</summary>
public sealed class LiveEntrySettings
{
    public string LiveRoot { get; set; } = string.Empty;
    public string PythonExecutable { get; set; } = "python";
}

public sealed class LiveEntryOptions
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }
    [JsonPropertyName("project_policy")]
    public string ProjectPolicy { get; set; } = "explicit_only";
    [JsonPropertyName("module_profiles")]
    public List<LiveModuleProfile> ModuleProfiles { get; set; } = new();
    [JsonPropertyName("targets")]
    public List<LiveEntryTarget> Targets { get; set; } = new();
}

public sealed class LiveModuleProfile
{
    [JsonPropertyName("module")]
    public string Module { get; set; } = string.Empty;
    [JsonPropertyName("access_modes")]
    public List<string> AccessModes { get; set; } = new();
    [JsonPropertyName("runtime_ready")]
    public bool RuntimeReady { get; set; }
    [JsonPropertyName("runtime_directory")]
    public string RuntimeDirectory { get; set; } = string.Empty;
}

public sealed class LiveEntryTarget
{
    [JsonIgnore]
    public string? ProjectEvarsPath { get; set; }
    [JsonPropertyName("project_code")]
    public string ProjectCode { get; set; } = string.Empty;
    [JsonPropertyName("mdb")]
    public string Mdb { get; set; } = string.Empty;
    [JsonPropertyName("module")]
    public string Module { get; set; } = string.Empty;
    [JsonPropertyName("access_modes")]
    public List<string> AccessModes { get; set; } = new();
    [JsonPropertyName("runtime_ready")]
    public bool RuntimeReady { get; set; }
    [JsonPropertyName("runtime_directory")]
    public string RuntimeDirectory { get; set; } = string.Empty;
}

public sealed class LiveEntryJob
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = string.Empty;
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;
    [JsonPropertyName("error")]
    public string? Error { get; set; }
    [JsonPropertyName("operator_action")]
    public string? OperatorAction { get; set; }
    [JsonPropertyName("session")]
    public LiveEntrySession? Session { get; set; }
}

public sealed class LiveEntrySession
{
    [JsonPropertyName("target_process_id")]
    public int TargetProcessId { get; set; }
    [JsonPropertyName("bridge_session_id")]
    public string BridgeSessionId { get; set; } = string.Empty;
    [JsonPropertyName("scope")]
    public LiveEntryScope Scope { get; set; } = new();
    [JsonPropertyName("access_mode")]
    public string AccessMode { get; set; } = string.Empty;
    [JsonPropertyName("gateway_url")]
    public string GatewayUrl { get; set; } = string.Empty;
    [JsonPropertyName("session_root")]
    public string SessionRoot { get; set; } = string.Empty;
}

public sealed class LiveEntryScope
{
    [JsonPropertyName("project_code")]
    public string ProjectCode { get; set; } = string.Empty;
    [JsonPropertyName("mdb")]
    public string Mdb { get; set; } = string.Empty;
    [JsonPropertyName("module")]
    public string Module { get; set; } = string.Empty;
}

public sealed class LiveEntrySessions
{
    [JsonPropertyName("sessions")]
    public List<LiveEntrySession> Sessions { get; set; } = new();
}

/// <summary>
/// Thin process adapter for the public e3d_live CLI. It neither imports Live source
/// nor edits SEP's legacy evars/launch settings.
/// </summary>
public sealed class LiveEntryAdapter
{
    private readonly string _settingsPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public LiveEntryAdapter(SepDataStore store)
    {
        _settingsPath = Path.Combine(store.DataDir, "live_entry.json");
    }

    public LiveEntrySettings LoadSettings()
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                return JsonSerializer.Deserialize<LiveEntrySettings>(File.ReadAllText(_settingsPath))
                    ?? new LiveEntrySettings();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Live entry settings could not be read: {ex.Message}", ex);
            }
        }
        return new LiveEntrySettings { LiveRoot = DetectSiblingRoot() };
    }

    public void SaveSettings(LiveEntrySettings settings)
    {
        string root = ValidateRoot(settings.LiveRoot);
        string python = ValidatePython(settings.PythonExecutable);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        string temporary = _settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new LiveEntrySettings
        {
            LiveRoot = root, PythonExecutable = python,
        }, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, _settingsPath, overwrite: true);
    }

    public async Task<LiveEntryOptions> GetOptionsAsync(LiveEntrySettings settings)
    {
        using var process = StartProcess(settings, "options");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Only the read-only options subprocess is terminated on timeout.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("The Live entry options request timed out.");
        }
        string output = await stdout;
        string errors = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Live entry options failed: {Tail(errors, output)}");
        var options = JsonSerializer.Deserialize<LiveEntryOptions>(output)
            ?? throw new InvalidOperationException("Live entry returned empty options.");
        if (options.SchemaVersion != 1 || options.ProjectPolicy is not ("explicit_only" or "any_valid_code") ||
            (options.ProjectPolicy == "any_valid_code" && options.ModuleProfiles.Count == 0) ||
            options.ModuleProfiles.Any(p => string.IsNullOrWhiteSpace(p.Module) || p.AccessModes.Count == 0) ||
            options.Targets.Any(t =>
            string.IsNullOrWhiteSpace(t.ProjectCode) || string.IsNullOrWhiteSpace(t.Mdb) ||
            string.IsNullOrWhiteSpace(t.Module) || t.AccessModes.Count == 0))
            throw new InvalidOperationException("Live entry returned an unsupported target schema.");
        return options;
    }

    public async Task<LiveEntryJob> StartAsync(LiveEntrySettings settings, LiveEntryTarget target, string mode)
    {
        if (!target.RuntimeReady || !target.AccessModes.Contains(mode, StringComparer.Ordinal))
            throw new InvalidOperationException("The selected Live target or access mode is unavailable.");
        // The CLI is the sole owner of scope validation, exact process binding and gateway startup.
        var arguments = new List<string> { "start", "--project-code", target.ProjectCode,
            "--mdb", target.Mdb, "--module", target.Module, "--access-mode", mode };
        if (!string.IsNullOrWhiteSpace(target.ProjectEvarsPath))
        {
            arguments.Add("--project-evars-path");
            arguments.Add(target.ProjectEvarsPath);
        }
        using var process = StartProcess(settings, arguments.ToArray());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await stdout;
        string errors = await stderr;
        string? last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        LiveEntryJob? job = null;
        try { if (last != null) job = JsonSerializer.Deserialize<LiveEntryJob>(last); }
        catch (JsonException) { }
        if (process.ExitCode != 0 || job?.State != "ready")
            throw new InvalidOperationException(job?.Error ?? Tail(errors, output));
        if (job.Session == null || job.Session.TargetProcessId <= 0)
            throw new InvalidOperationException("Live entry reported ready without a verified target PID.");
        return job;
    }

    public async Task<LiveEntrySessions> GetSessionsAsync(LiveEntrySettings settings)
    {
        (string output, string errors, int exitCode) = await RunShortCommandAsync(settings, "sessions");
        if (exitCode != 0) throw new InvalidOperationException($"Live session query failed: {Tail(errors, output)}");
        return JsonSerializer.Deserialize<LiveEntrySessions>(output)
            ?? throw new InvalidOperationException("Live session query returned no data.");
    }

    public async Task StopAsync(LiveEntrySettings settings, LiveEntrySession session)
    {
        (string output, string errors, int exitCode) = await RunShortCommandAsync(settings,
            "stop", "--bridge-session-id", session.BridgeSessionId,
            "--target-process-id", session.TargetProcessId.ToString());
        if (exitCode != 0) throw new InvalidOperationException($"Live stop failed: {Tail(errors, output)}");
        using var document = JsonDocument.Parse(output);
        if (document.RootElement.GetProperty("state").GetString() != "stopped")
            throw new InvalidOperationException("Live stop was not acknowledged.");
    }

    private static async Task<(string Output, string Errors, int ExitCode)> RunShortCommandAsync(
        LiveEntrySettings settings, params string[] arguments)
    {
        using var process = StartProcess(settings, arguments);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Live entry request timed out.");
        }
        return (await stdout, await stderr, process.ExitCode);
    }

    private static Process StartProcess(LiveEntrySettings settings, params string[] arguments)
    {
        string root = ValidateRoot(settings.LiveRoot);
        var info = new ProcessStartInfo(ValidatePython(settings.PythonExecutable))
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("-m");
        info.ArgumentList.Add("integrations.e3d_live.live_entry");
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        try { return Process.Start(info) ?? throw new InvalidOperationException("Python did not start."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            throw new InvalidOperationException($"Could not start the Live entry interpreter: {ex.Message}", ex);
        }
    }

    private static string ValidateRoot(string value)
    {
        string normalized = SepPaths.Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Select the e3d_live repository folder first.");
        string root = Path.GetFullPath(normalized);
        if (!File.Exists(Path.Combine(root, "integrations", "e3d_live", "live_entry.py")))
            throw new InvalidOperationException("Select the e3d_live repository root containing integrations/e3d_live/live_entry.py.");
        return root;
    }

    private static string ValidatePython(string value)
    {
        string python = value.Trim();
        if (python.Length == 0 || python.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            throw new InvalidOperationException("Set a valid Python executable or command name.");
        return python;
    }

    private static string DetectSiblingRoot()
    {
        string? env = Environment.GetEnvironmentVariable("E3D_LIVE_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(Path.Combine(env, "integrations", "e3d_live", "live_entry.py")))
            return Path.GetFullPath(env);
        foreach (string relative in new[] { @"..\e3d_live", @"..\..\e3d_live" })
        {
            string root = Path.GetFullPath(Path.Combine(SepDataStore.ExeDir, relative));
            if (File.Exists(Path.Combine(root, "integrations", "e3d_live", "live_entry.py"))) return root;
        }
        return string.Empty;
    }

    private static string Tail(string first, string second)
    {
        string text = string.IsNullOrWhiteSpace(first) ? second : first;
        return text.Length > 2000 ? text[^2000..] : text.Trim();
    }
}
