using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace SEP.App.Services;

/// <summary>
/// Headless plug-in setup for installers (the E3DHUB "Install E3D tools" download):
///
///   SEP.exe --apply-plugins=C:\path\request.json
///
/// request.json: { "plugins": ["E3DHUB", ...],          enable these (folders under pluginsDir)
///                 "disable": ["OldPlugin", ...],        switch these off (their add-ins leave E3D)
///                 "removeRibbonIfUnused": false,        nothing left enabled: remove SEP.uic from E3D too
///                 "pluginsDir": "D:\\AVEVA\\Plugins", "e3dHome": "D:\\AVEVA\\Everything3D3.1",
///                 "projectsDir": "D:\\AVEVA\\Projects\\E3D3.1", "result": "C:\\path\\result.json" }
///
/// The paths are optional; given ones are saved to SEP's settings, so the SEP window later shows the same
/// setup. Every other plug-in keeps its state. Enabling is exactly what the Plug-ins page does: pml.index
/// rebuilt, custom_evars.bat managed block, SEP.uic ribbon, add-in DLLs linked into E3D and DesignAddins.xml.
/// No window opens, also not on an error (App.Headless). Exit code 0 = done as asked, 1 = some plug-ins were
/// not found or not deployed, or the write failed, 2 = the request could not be read. Details: the result file.
/// </summary>
public static class PluginCli
{
    public const string Switch = "--apply-plugins=";

    public sealed class Request
    {
        [JsonPropertyName("plugins")] public List<string> Plugins { get; set; } = new();
        [JsonPropertyName("disable")] public List<string> Disable { get; set; } = new();
        [JsonPropertyName("removeRibbonIfUnused")] public bool RemoveRibbonIfUnused { get; set; }
        [JsonPropertyName("pluginsDir")] public string? PluginsDir { get; set; }
        [JsonPropertyName("e3dHome")] public string? E3dHome { get; set; }
        [JsonPropertyName("projectsDir")] public string? ProjectsDir { get; set; }
        [JsonPropertyName("result")] public string? Result { get; set; }
    }

    public sealed class Result
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
        [JsonPropertyName("enabled")] public List<string> Enabled { get; set; } = new();
        [JsonPropertyName("disabled")] public List<string> Disabled { get; set; } = new();
        [JsonPropertyName("missing")] public List<string> Missing { get; set; } = new();
        [JsonPropertyName("addins")] public List<string> Addins { get; set; } = new();
        [JsonPropertyName("addinsNotDeployed")] public List<string> AddinsNotDeployed { get; set; } = new();
        [JsonPropertyName("addinsLeftInE3d")] public List<string> AddinsLeftInE3d { get; set; } = new();
        [JsonPropertyName("ribbonRemoved")] public bool RibbonRemoved { get; set; }
        [JsonPropertyName("pluginsDir")] public string PluginsDir { get; set; } = string.Empty;
        [JsonPropertyName("customEvars")] public string CustomEvars { get; set; } = string.Empty;
        [JsonPropertyName("e3dHome")] public string E3dHome { get; set; } = string.Empty;
        [JsonPropertyName("ribbon")] public string Ribbon { get; set; } = string.Empty;
    }

    public static bool IsHeadless(IEnumerable<string> args) => args.Any(a => a.StartsWith(Switch, StringComparison.OrdinalIgnoreCase));

    public static string? RequestPath(IEnumerable<string> args)
    {
        string? arg = args.FirstOrDefault(a => a.StartsWith(Switch, StringComparison.OrdinalIgnoreCase));
        return arg?[Switch.Length..].Trim().Trim('"');
    }

    public static int Run(IServiceProvider services, string requestPath)
    {
        Request? request = null;
        var result = new Result();
        int code;
        try
        {
            request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath, Encoding.UTF8));
            if (request == null) throw new InvalidDataException("empty request");
            code = Apply(services, request, result);
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.Message = $"Could not apply the plug-ins: {ex.Message}";
            code = request == null ? 2 : 1;
        }
        App.Log($"[apply-plugins] {result.Message}");
        WriteResult(request?.Result, requestPath, result);
        return code;
    }

    /// <summary>Also used when SEP fails in a way Run could not catch, so the installer still gets an answer.</summary>
    public static void WriteFailure(IEnumerable<string> args, string message)
    {
        try
        {
            string? path = RequestPath(args);
            if (path == null) return;
            string? resultPath = null;
            try { resultPath = JsonSerializer.Deserialize<Request>(File.ReadAllText(path, Encoding.UTF8))?.Result; } catch { }
            WriteResult(resultPath, path, new Result { Ok = false, Message = message });
        }
        catch { }
    }

    private static void WriteResult(string? resultPath, string requestPath, Result result)
    {
        string path = !string.IsNullOrWhiteSpace(resultPath) ? resultPath! : Path.ChangeExtension(requestPath, ".result.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            App.Log($"[apply-plugins] cannot write {path}: {ex.Message}");
        }
    }

    private static int Apply(IServiceProvider services, Request request, Result result)
    {
        // Paths first: PluginService reads them when it is created.
        var catalog = services.GetRequiredService<IProjectCatalog>();
        string e3dHome = SepPaths.Normalize(request.E3dHome);
        if (e3dHome.Length > 0)
        {
            if (!File.Exists(Path.Combine(e3dHome, "des.exe"))) throw new DirectoryNotFoundException($"E3D Design not found in {e3dHome}");
            catalog.Paths.InstallDir = e3dHome;
            string evars = Path.Combine(e3dHome, "evars.bat");
            if (File.Exists(evars)) catalog.Paths.EvarsBat = evars;
        }
        string projectsDir = SepPaths.Normalize(request.ProjectsDir);
        if (projectsDir.Length > 0)
        {
            catalog.Paths.ProjectsDir = projectsDir;
            catalog.Data.Settings.LocalProjectsDir = projectsDir;
        }
        string pluginsDir = SepPaths.Normalize(request.PluginsDir);
        if (pluginsDir.Length > 0) catalog.Data.Settings.PluginsDir = pluginsDir;
        catalog.Save();

        var plugins = services.GetRequiredService<PluginService>();
        result.PluginsDir = plugins.PluginsDir;
        result.CustomEvars = plugins.CustomEvarsPath;
        result.E3dHome = catalog.Paths.InstallDir ?? string.Empty;

        static List<string> Clean(IEnumerable<string> names) => names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var wanted = Clean(request.Plugins);
        var unwanted = Clean(request.Disable).Where(n => !wanted.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();

        var found = plugins.ScanAsync(autoHeal: true).GetAwaiter().GetResult();
        var byName = found.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        result.Missing = wanted.Where(n => !byName.ContainsKey(n)).ToList();
        var present = wanted.Where(byName.ContainsKey).Select(n => byName[n].Name).ToList();
        // add-ins of the plug-ins switched off, to check they left E3D (read before their folders may go)
        var offAddins = unwanted.Where(byName.ContainsKey).SelectMany(n => byName[n].AddinAssemblies.Select(a => a.Name)).ToList();

        var write = plugins.ChangeEnabledAsync(present, unwanted).GetAwaiter().GetResult();
        result.Enabled = plugins.ReadEnabled();
        result.Disabled = unwanted.Where(n => !result.Enabled.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (request.RemoveRibbonIfUnused) result.RibbonRemoved = plugins.RemoveRibbonIfUnused();

        // Check what E3D will see at its next start.
        string home = result.E3dHome;
        var stillOn = found.Where(p => result.Enabled.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).SelectMany(p => p.AddinAssemblies.Select(a => a.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (home.Length > 0)
        {
            string ribbon = Path.Combine(home, "SEP.uic");
            if (File.Exists(ribbon)) result.Ribbon = ribbon;
            string designAddins = Path.Combine(home, "DesignAddins.xml");
            string xml = File.Exists(designAddins) ? File.ReadAllText(designAddins) : string.Empty;
            bool Listed(string asm) => xml.Contains($"<string>{asm}</string>", StringComparison.OrdinalIgnoreCase);
            foreach (var name in present)
            {
                foreach (var asm in byName[name].AddinAssemblies)
                {
                    result.Addins.Add(asm.Name);
                    if (!File.Exists(Path.Combine(home, asm.Name + ".dll")) || !Listed(asm.Name)) result.AddinsNotDeployed.Add(asm.Name);
                }
            }
            foreach (var asm in offAddins.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!stillOn.Contains(asm) && Listed(asm)) result.AddinsLeftInE3d.Add(asm);
            }
        }

        var notEnabled = present.Where(n => !result.Enabled.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        var notDisabled = unwanted.Where(n => result.Enabled.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        result.Ok = write.Ok && result.Missing.Count == 0 && notEnabled.Count == 0 && notDisabled.Count == 0
            && result.AddinsNotDeployed.Count == 0 && result.AddinsLeftInE3d.Count == 0;
        var parts = new List<string> { write.Message };
        if (result.Missing.Count > 0) parts.Add($"not found in {result.PluginsDir}: {string.Join(", ", result.Missing)}");
        if (notEnabled.Count > 0) parts.Add($"not enabled: {string.Join(", ", notEnabled)}");
        if (notDisabled.Count > 0) parts.Add($"still enabled: {string.Join(", ", notDisabled)}");
        if (result.AddinsNotDeployed.Count > 0) parts.Add($"add-ins not deployed into E3D: {string.Join(", ", result.AddinsNotDeployed)}");
        if (result.AddinsLeftInE3d.Count > 0) parts.Add($"add-ins still listed in E3D: {string.Join(", ", result.AddinsLeftInE3d)}");
        result.Message = string.Join("; ", parts);
        return result.Ok ? 0 : 1;
    }
}
