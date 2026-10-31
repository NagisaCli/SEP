using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SEP.App.Models;

namespace SEP.App.Services;

/// <summary>
/// The E3D side of the plug-ins: the SEP ribbon tab (SEP.uic) and the plug-in panel.
///
/// With the panel (the default) the ribbon tab stays small: a "Plug-ins" button opens a docked panel
/// (the SEP.PluginPanel add-in, embedded in SEP and deployed into the E3D folder) that lists every enabled
/// plug-in with its status and the commands it supplies; you choose there which ones are buttons, pin them,
/// rename them and give them shortcuts. SEP describes the plug-ins in SEP.PluginPanel.json next to the add-in.
/// The classic layout (every plug-in's buttons on the tab) is kept for PCs without the add-in and as a setting.
/// </summary>
public sealed partial class PluginService
{
    public const string PanelAddinName = "SEP.PluginPanel";
    public const string PanelManifestFile = "SEP.PluginPanel.json";
    private const string PanelResource = "SEP.PluginPanel.dll";

    /// <summary>SEP's ribbon icons: "SEPIcons:ID_…" in SEP.uic, read by E3D from SEPIcons.resources next to des.exe.</summary>
    private const string IconSetName = "SEPIcons";

    /// <summary>Plug-ins that work without buttons (keyboard / background): the panel shows their status only.</summary>
    private static readonly HashSet<string> StatusOnlyByDefault = new(StringComparer.OrdinalIgnoreCase) { "VVIEW" };

    /// <summary>Featured (button) commands per plug-in before the rest go under "More".</summary>
    private const int MaxFeatured = 6;

    /// <summary>"panel" (compact tab + plug-in panel) or "classic" (every plug-in's buttons on the tab).</summary>
    public string RibbonMode
    {
        get => string.Equals(_catalog.Data.Settings.E3dRibbon, "classic", StringComparison.OrdinalIgnoreCase) ? "classic" : "panel";
        set
        {
            _catalog.Data.Settings.E3dRibbon = value == "classic" ? "classic" : "panel";
            _catalog.Save();
        }
    }

    /// <summary>Whether this SEP build carries the panel add-in (it is only built where E3D is installed).</summary>
    public static bool HasPanelAddin => PanelAddinBytes.Value != null;

    private static readonly Lazy<byte[]?> PanelAddinBytes = new(() =>
    {
        try
        {
            using var s = typeof(PluginService).Assembly.GetManifestResourceStream(PanelResource);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] bytes = ms.ToArray();
            return HasType(bytes, "PluginPanelAddin") ? bytes : null;
        }
        catch (Exception ex)
        {
            App.Log($"panel add-in resource: {ex.Message}");
            return null;
        }
    });

    private static bool HasType(byte[] assembly, string typeName)
    {
        using var pe = new PEReader(new MemoryStream(assembly));
        if (!pe.HasMetadata) return false;
        var md = pe.GetMetadataReader();
        return md.TypeDefinitions.Any(h => md.GetString(md.GetTypeDefinition(h).Name) == typeName);
    }

    // ── what a plug-in contributes ───────────────────────────────────────────────

    /// <summary>A plug-in's contribution: ribbon parts for SEP.uic and the commands the panel offers.</summary>
    private sealed class PluginTools
    {
        public List<string> FormsMeta { get; } = new();
        public List<KeyValuePair<string, string>> Tools { get; } = new();
        public List<string> Groups { get; } = new();
        public List<PanelActionDto> Actions { get; } = new();
    }

    private PluginTools CollectTools(string name, string folder, PluginInfo info)
    {
        var result = new PluginTools();
        string safeName = Regex.Replace(name, @"[^a-zA-Z0-9_]", "_");
        string normFolder = folder.Replace('\\', '/').TrimEnd('/');

        // Look for .uic file in resolved version folder or plugin root
        string? uicPath = null;
        if (info.ResolvedVersionDir != null && Directory.Exists(info.ResolvedVersionDir))
            uicPath = Directory.EnumerateFiles(info.ResolvedVersionDir, "*.uic", SearchOption.AllDirectories).FirstOrDefault();
        uicPath ??= Directory.EnumerateFiles(folder, "*.uic", SearchOption.AllDirectories)
            .OrderBy(f => Path.GetDirectoryName(f) == folder ? 0 : 1)
            .FirstOrDefault();

        if (uicPath != null && File.Exists(uicPath))
        {
            try
            {
                var doc = XDocument.Load(uicPath);
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

                // 1. FormsMetaData
                var formsMetaElem = doc.Descendants(ns + "FormsMetaData").FirstOrDefault();
                if (formsMetaElem != null)
                {
                    foreach (var fm in formsMetaElem.Elements(ns + "FormMetaData"))
                    {
                        string fmXml = Regex.Replace(fm.ToString(), @"\s*xmlns=""[^""]*""", "");
                        result.FormsMeta.Add("    " + fmXml.Trim());
                    }
                }

                // 2. Tools (full original titles, tooltips and attributes)
                var tools = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
                var toolsElem = doc.Descendants(ns + "Tools").FirstOrDefault();
                if (toolsElem != null)
                {
                    foreach (var tool in toolsElem.Elements())
                    {
                        string toolName = tool.Attribute("Name")?.Value ?? string.Empty;
                        if (string.IsNullOrEmpty(toolName)) continue;

                        // Safely redirect outdated macro paths ONLY if the original target does not exist
                        foreach (var macroElem in tool.Descendants(ns + "Macro"))
                        {
                            var match = Regex.Match(macroElem.Value.Trim(), @"(?i)^\$m\s+([A-Za-z]:[\\/][^ \r\n\t]+)");
                            if (!match.Success) continue;
                            string full = match.Groups[1].Value.Replace('\\', '/');
                            if (File.Exists(full)) continue;
                            int pmlIdx = full.IndexOf("/pmllib/", StringComparison.OrdinalIgnoreCase);
                            if (pmlIdx < 0) pmlIdx = full.IndexOf("/pml/", StringComparison.OrdinalIgnoreCase);
                            int pdmsuiIdx = full.IndexOf("/pdmsui/", StringComparison.OrdinalIgnoreCase);
                            int idx = pmlIdx >= 0 ? pmlIdx : pdmsuiIdx;
                            if (idx < 0) continue;
                            string candidate = Path.Combine(folder, full[(idx + 1)..].Replace('/', '\\'));
                            if (File.Exists(candidate)) macroElem.Value = $"$m {normFolder}{full[idx..]}";
                        }

                        tools[toolName] = tool;
                        string toolXml = Regex.Replace(tool.ToString(), @"\s*xmlns=""[^""]*""", "");
                        result.Tools.Add(new(toolName, "    " + toolXml.Trim()));
                    }
                }

                // 3. Groups of the plug-in's own tab(s), and which tools it shows large
                var groups = doc.Descendants(ns + "Tabs").FirstOrDefault()?.Descendants(ns + "Group").ToList() ?? new();
                var inGroups = new List<(string Tool, bool Large, string Group)>();
                foreach (var g in groups)
                {
                    string gName = g.Attribute("Name")?.Value ?? $"SEP.Group.{safeName}";
                    string gCaption = g.Element(ns + "Caption")?.Value?.Trim() ?? PluginInfo.FormatDefaultDisplayName(name);
                    var toolsInGroup = g.Descendants(ns + "Tool").ToList();
                    if (toolsInGroup.Count == 0) continue;

                    var groupSb = new StringBuilder();
                    groupSb.AppendLine($"        <Group Name=\"{gName}\">");
                    groupSb.AppendLine("          <Tools>");
                    foreach (var tg in toolsInGroup)
                    {
                        string tn = tg.Attribute("Name")?.Value ?? string.Empty;
                        string isLarge = tg.Attribute("LargeImage")?.Value ?? "False";
                        string imgOnly = tg.Attribute("ImageOnly")?.Value ?? "False";
                        if (string.IsNullOrEmpty(tn) || !tools.ContainsKey(tn)) continue;
                        groupSb.AppendLine($"            <Tool Name=\"{tn}\" LargeImage=\"{isLarge}\" ImageOnly=\"{imgOnly}\" />");
                        inGroups.Add((tn, isLarge.Equals("True", StringComparison.OrdinalIgnoreCase), gCaption));
                    }
                    groupSb.AppendLine("          </Tools>");
                    groupSb.AppendLine($"          <Caption>{System.Security.SecurityElement.Escape(gCaption)}</Caption>");
                    groupSb.AppendLine("        </Group>");
                    result.Groups.Add(groupSb.ToString());
                }

                // 4. The panel's commands: the tools on the plug-in's tab first (large ones as buttons), then the rest
                var ordered = inGroups.Select(t => t.Tool).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Concat(tools.Keys.Where(k => !inGroups.Any(t => t.Tool.Equals(k, StringComparison.OrdinalIgnoreCase))))
                    .ToList();
                bool anyLarge = inGroups.Any(t => t.Large);
                foreach (string toolName in ordered)
                {
                    var spec = ToAction(tools[toolName], ns);
                    if (spec == null) continue;
                    var placement = inGroups.FirstOrDefault(t => t.Tool.Equals(toolName, StringComparison.OrdinalIgnoreCase));
                    spec.Group = placement.Group;
                    bool onTab = placement.Tool != null;
                    spec.Featured = result.Actions.Count(a => a.Featured) < MaxFeatured && onTab && (placement.Large || !anyLarge && result.Actions.Count < 4);
                    result.Actions.Add(spec);
                }

                if (result.Groups.Count > 0) return result;
            }
            catch (Exception ex)
            {
                App.Log($"Parsing plugin UIC failed for {name}: {ex.Message}");
            }
        }

        AddGeneratedTools(result, name, safeName, info);
        return result;
    }

    /// <summary>The panel's view of one .uic tool, or null when the panel cannot run it (combo boxes, galleries…).</summary>
    private static PanelActionDto? ToAction(XElement tool, XNamespace ns)
    {
        string kind = tool.Name.LocalName;
        if (kind is not ("ButtonTool" or "StateButtonTool")) return null;
        var command = tool.Element(ns + "Command");
        string type = command?.Element(ns + "Type")?.Value?.Trim() ?? string.Empty;
        string? run = type.Equals("Macro", StringComparison.OrdinalIgnoreCase)
            ? command?.Element(ns + "Macro")?.Value?.Trim()
            : command?.Element(ns + "Key")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(run)) return null;
        string caption = tool.Element(ns + "Caption")?.Value?.Trim() ?? tool.Attribute("Name")!.Value;
        string tooltip = tool.Element(ns + "Tooltip")?.Value?.Trim() ?? string.Empty;
        return new PanelActionDto
        {
            Id = tool.Attribute("Name")!.Value,
            Caption = caption,
            Tooltip = tooltip,
            Kind = type.Equals("Macro", StringComparison.OrdinalIgnoreCase) ? "macro" : "command",
            Command = run,
            Image = tool.Element(ns + "ImageResourceId")?.Value?.Trim(),
        };
    }

    /// <summary>A plug-in without a ribbon of its own: buttons from its launcher macro, forms, macros or functions.</summary>
    private void AddGeneratedTools(PluginTools result, string name, string safeName, PluginInfo info)
    {
        var autoTools = new List<string>();

        void Add(string key, string macro, string caption, string tooltip, bool featured, bool panel = true)
        {
            string esc(string s) => System.Security.SecurityElement.Escape(s) ?? string.Empty;
            string toolXml = $@"    <ButtonTool Name=""{key}"">
      <Command>
        <Type>Macro</Type>
        <Macro>{esc(macro)}</Macro>
        <Key>{key}</Key>
        <Arguments />
      </Command>
      <ImageResourceId>AvevaSharedIcons:ID_APPLICATION_RUN</ImageResourceId>
      <Caption>{esc(caption)}</Caption>
      <HelpContext />
      <Tooltip>{esc(tooltip)}</Tooltip>
      <DisplayStyle>Default</DisplayStyle>
      <Category>SEP</Category>
    </ButtonTool>";
            if (result.Tools.Any(t => t.Key == key)) return;
            result.Tools.Add(new(key, toolXml));
            autoTools.Add(key);
            if (panel)
                result.Actions.Add(new PanelActionDto { Id = key, Caption = caption, Tooltip = tooltip, Kind = "macro", Command = macro, Featured = featured });
        }

        // 1. a dedicated launcher macro (e.g. launch_pipe_aligner.mac) is the main button
        var launcherMacro = info.Macros.FirstOrDefault(m => Regex.IsMatch(m.Name, @"(?i)^(?:launch|start|run)_"));
        if (launcherMacro != null)
        {
            string macroPath = launcherMacro.Path.Replace('\\', '/');
            Add($"SEP.{safeName}.Launcher_{launcherMacro.Name}", $"$m {macroPath}", info.EffectiveDisplayName,
                $"Launch {info.EffectiveDisplayName} ($m {macroPath})", featured: true);
        }

        foreach (var f in info.Forms)
            Add($"SEP.{safeName}.Form_{f.Name}", $"show !!{f.Name}", f.Name, $"Open {f.Name} form", featured: autoTools.Count < 3);

        foreach (var m in info.Macros.Where(m => launcherMacro == null || !m.Path.Equals(launcherMacro.Path, StringComparison.OrdinalIgnoreCase)).Take(6))
        {
            string caption = m.Name.Equals("ExportPml", StringComparison.OrdinalIgnoreCase) ? "Export PML" : m.Name;
            Add($"SEP.{safeName}.Macro_{m.Name}", $"$m {m.Path.Replace('\\', '/')}", caption, $"Execute macro {m.Name}", featured: autoTools.Count == 0);
        }

        // no forms or macros: its public PML functions
        if (autoTools.Count == 0)
            foreach (var fn in info.Functions.Take(4))
                if (!string.IsNullOrWhiteSpace(fn.CallCommand))
                    Add($"SEP.{safeName}.Func_{fn.Name}", fn.CallCommand!, fn.Name, $"Execute function {fn.Name}", featured: false);

        // an add-in alone gets a ribbon button that only reports it is active; the panel shows its status instead
        if (autoTools.Count == 0 && info.HasAddin)
            foreach (var asm in info.AddinAssemblies)
                Add($"SEP.{safeName}.Addin_{asm.Name}", $"$P [SEP] {asm.Name} add-in is active", info.EffectiveDisplayName, $"AVEVA Add-in: {asm.Name}", featured: false, panel: false);

        if (autoTools.Count == 0) return;
        string? customName = _catalog.GetPluginDisplayName(name);
        string groupCaption = !string.IsNullOrWhiteSpace(customName) ? customName : PluginInfo.FormatDefaultDisplayName(name);
        var sb = new StringBuilder();
        sb.AppendLine($"        <Group Name=\"SEP.Group.{safeName}\">");
        sb.AppendLine("          <Tools>");
        for (int i = 0; i < autoTools.Count; i++)
            sb.AppendLine($"            <Tool Name=\"{autoTools[i]}\" LargeImage=\"{(i < 2 ? "True" : "False")}\" ImageOnly=\"False\" />");
        sb.AppendLine("          </Tools>");
        sb.AppendLine($"          <Caption>{System.Security.SecurityElement.Escape(groupCaption)}</Caption>");
        sb.AppendLine("        </Group>");
        result.Groups.Add(sb.ToString());
    }

    // ── SEP.uic and the panel's list ─────────────────────────────────────────────

    private void GenerateSepUic(string installDir, HashSet<string> enabled, IReadOnlyList<PluginInfo>? allPlugins = null)
    {
        // the ribbon button must start a copy that stays put: the installed one when there is one
        string sepExePath = SepInstaller.StableExePath;
        string sepExeEscaped = sepExePath.Replace("\\", "/");
        bool usePanel = RibbonMode == "panel" && DeployPanelAddin(installDir);
        // E3D refuses the whole tab when an image's resource file is missing: AVEVA's icons unless ours are there
        bool ownIcons = DeployRibbonIcons(installDir);
        string Icon(string own, string aveva) => ownIcons ? $"{IconSetName}:{own}" : $"AvevaSharedIcons:{aveva}";

        var formsMeta = new List<string>();
        var toolsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupsList = new List<string>();
        var panelPlugins = new List<PanelPluginDto>();

        // Find all plugin directories
        var allPluginDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string root = PluginsDir;
        if (Directory.Exists(root))
        {
            foreach (var d in Directory.EnumerateDirectories(root))
            {
                string fn = Path.GetFileName(d);
                if (!fn.StartsWith('.')) allPluginDirs[fn] = d;
            }
        }
        foreach (var d in DiscoverPluginDirsFromCustomEvars())
        {
            string fn = Path.GetFileName(d);
            if (!allPluginDirs.ContainsKey(fn)) allPluginDirs[fn] = d;
        }

        foreach (var name in enabled.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (!allPluginDirs.TryGetValue(name, out var folder) || !Directory.Exists(folder)) continue;
            var info = allPlugins?.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? Inspect(folder, installDir, _catalog.Paths.E3dVersion);
            bool noUi = File.Exists(Path.Combine(folder, ".noui")) || File.Exists(Path.Combine(folder, "NO_UI"));
            var tools = noUi ? new PluginTools() : CollectTools(name, folder, info);

            foreach (var fm in tools.FormsMeta) if (!formsMeta.Contains(fm)) formsMeta.Add(fm);
            foreach (var t in tools.Tools) toolsMap[t.Key] = t.Value;
            if (!usePanel) groupsList.AddRange(tools.Groups);
            panelPlugins.Add(ToPanelPlugin(name, folder, info, tools, noUi));
        }

        string Tool(string key, string type, string command, string image, string caption, string tooltip) => $@"    <ButtonTool Name=""{key}"">
      <Command>
        <Type>{type}</Type>
        {(type == "Macro" ? $"<Macro>{command}</Macro>" : string.Empty)}
        <Key>{(type == "Macro" ? key : command)}</Key>
        <Arguments />
      </Command>
      <ImageResourceId>{image}</ImageResourceId>
      <Caption>{caption}</Caption>
      <HelpContext />
      <Tooltip>{tooltip}</Tooltip>
      <DisplayStyle>Default</DisplayStyle>
      <Category>SEP</Category>
    </ButtonTool>";

        toolsMap["SEP.Tools.PmlRehash"] = Tool("SEP.Tools.PmlRehash", "Macro", "PML REHASH ALL", Icon("ID_SEP_REFRESH", "ID_REFRESH"),
            "Refresh PML", "Reload the PML libraries (PML REHASH ALL)");
        toolsMap["SEP.Tools.OpenSep"] = Tool("SEP.Tools.OpenSep", "Macro", $"syscom 'start \"\" \"{sepExeEscaped}\"'", Icon("ID_SEP_APP", "ID_APPLICATION_RUN"),
            "SEP", "Open SEP, the E3D project and plug-in manager");

        if (usePanel)
        {
            // one large button for the floating bar, the rest small beside it, SEP at the end
            toolsMap["SEP.Tools.PluginBar"] = Tool("SEP.Tools.PluginBar", "Instance", "SEP.PluginBar.Toggle", Icon("ID_SEP_BAR", "ID_APPLICATION_RUN"),
                "Plug-ins", "Show or hide the plug-in bar: your pinned commands and every enabled plug-in, floating over E3D");
            toolsMap["SEP.Tools.PluginSearch"] = Tool("SEP.Tools.PluginSearch", "Instance", "SEP.PluginBar.Search", Icon("ID_SEP_FIND", "ID_FIND"),
                "Find command", "Search every plug-in command from the plug-in bar");
            toolsMap["SEP.Tools.PluginPanel"] = Tool("SEP.Tools.PluginPanel", "Instance", "SEP.PluginPanel.Show", Icon("ID_SEP_MANAGE", "ID_SETTINGS"),
                "Manage plug-ins", "Side panel: plug-in status, which commands are buttons, pins and shortcuts");
            groupsList.Add(@"        <Group Name=""SEP.Group.Plugins"">
          <Tools>
            <Tool Name=""SEP.Tools.PluginBar"" LargeImage=""True"" ImageOnly=""False"" />
            <Tool Name=""SEP.Tools.PluginSearch"" LargeImage=""False"" ImageOnly=""False"" />
            <Tool Name=""SEP.Tools.PluginPanel"" LargeImage=""False"" ImageOnly=""False"" />
            <Tool Name=""SEP.Tools.PmlRehash"" LargeImage=""False"" ImageOnly=""False"" />
            <Tool Name=""SEP.Tools.OpenSep"" LargeImage=""True"" ImageOnly=""False"" />
          </Tools>
          <Caption>SEP</Caption>
        </Group>
");
        }
        else
        {
            groupsList.Add(@"        <Group Name=""SEP.Group.Maintenance"">
          <Tools>
            <Tool Name=""SEP.Tools.PmlRehash"" LargeImage=""True"" ImageOnly=""False"" />
            <Tool Name=""SEP.Tools.OpenSep"" LargeImage=""True"" ImageOnly=""False"" />
          </Tools>
          <Caption>SEP</Caption>
        </Group>
");
        }

        string fullUicXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<UserInterfaceCustomization xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns=""www.aveva.com"">
  <Version>1.0</Version>
  <FormsMetaData>
{string.Concat(formsMeta.Select(f => f + "\r\n"))}  </FormsMetaData>
  <Tools>
{string.Concat(toolsMap.Values.Select(t => t + "\r\n"))}  </Tools>
  <InstanceTools />
  <MenuBar />
  <CommandBars />
  <TaskPanes />
  <ContextMenus />
  <AreaLeftTools />
  <AreaRightTools />
  <FooterTools />
  <NavigationMenuTools />
  <QATTools />
  <ContextualTabGroups />
  <TabToolbarTools />
  <Tabs>
    <Tab Name=""SEP.Tab"">
      <Caption>SEP</Caption>
      <Groups>
{string.Concat(groupsList)}      </Groups>
    </Tab>
  </Tabs>
  <MiniToolbarTools />
  <Namespace>SEP</Namespace>
</UserInterfaceCustomization>
";
        File.WriteAllText(Path.Combine(installDir, "SEP.uic"), fullUicXml, new UTF8Encoding(false));
        WritePanelManifest(installDir, panelPlugins, sepExePath);
    }

    private PanelPluginDto ToPanelPlugin(string name, string folder, PluginInfo info, PluginTools tools, bool noUi)
    {
        var declared = ReadPluginPanelHints(folder);
        string? customName = _catalog.GetPluginDisplayName(name);
        string mode = declared.Mode
            ?? (noUi || tools.Actions.Count == 0 || StatusOnlyByDefault.Contains(name) ? "status" : "buttons");
        var pmlPaths = new List<string>();
        if (info.PmlLibPath != null) pmlPaths.Add(info.PmlLibPath);
        return new PanelPluginDto
        {
            Name = name,
            DisplayName = !string.IsNullOrWhiteSpace(customName) ? customName : info.EffectiveDisplayName,
            Description = declared.Description ?? ReadmeSummary(folder)
                ?? tools.Actions.Select(a => a.Tooltip).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
            Path = folder,
            Mode = mode,
            Addins = info.AddinAssemblies.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            PmlPaths = pmlPaths,
            Actions = tools.Actions,
        };
    }

    /// <summary>
    /// Optional <c>sep-plugin.json</c> in a plug-in folder: <c>{ "panel": { "mode": "status", "description": "…" } }</c>
    /// lets a plug-in say how the panel should show it by default.
    /// </summary>
    private static (string? Mode, string? Description) ReadPluginPanelHints(string folder)
    {
        try
        {
            string file = Path.Combine(folder, "sep-plugin.json");
            if (!File.Exists(file)) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("panel", out var panel) || panel.ValueKind != JsonValueKind.Object) return (null, null);
            string? mode = panel.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()?.ToLowerInvariant() : null;
            if (mode is not ("buttons" or "status" or "hidden")) mode = null;
            string? description = panel.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            return (mode, string.IsNullOrWhiteSpace(description) ? null : description.Trim());
        }
        catch (Exception ex)
        {
            App.Log($"sep-plugin.json in {folder}: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>The first prose paragraph of the plug-in's README (headings, badges and code skipped), shortened.</summary>
    private static string? ReadmeSummary(string folder)
    {
        try
        {
            string? readme = Directory.EnumerateFiles(folder, "README*.md").FirstOrDefault();
            if (readme == null) return null;
            bool inCode = false;
            var para = new List<string>();
            foreach (string raw in File.ReadLines(readme).Take(80))
            {
                string line = raw.Trim();
                if (line.StartsWith("```", StringComparison.Ordinal)) { inCode = !inCode; continue; }
                if (inCode) continue;
                if (line.Length == 0) { if (para.Count > 0) break; continue; }
                if (line.StartsWith('#') || line.StartsWith('!') || line.StartsWith('|') || line.StartsWith('>') || line.StartsWith("<", StringComparison.Ordinal)) { if (para.Count > 0) break; continue; }
                para.Add(line);
            }
            if (para.Count == 0) return null;
            string text = Regex.Replace(string.Join(" ", para), @"\[([^\]]*)\]\([^)]*\)", "$1").Replace("**", "").Replace("`", "");
            return text.Length <= 180 ? text : text[..177].TrimEnd() + "…";
        }
        catch { return null; }
    }

    private static void WritePanelManifest(string installDir, List<PanelPluginDto> plugins, string sepExe)
    {
        try
        {
            var manifest = new PanelManifestDto
            {
                GeneratedAt = DateTime.Now.ToString("s"),
                SepExe = sepExe,
                PluginsDir = plugins.Count > 0 ? Path.GetDirectoryName(plugins[0].Path) : null,
                Plugins = plugins,
            };
            string json = JsonSerializer.Serialize(manifest, PanelJson);
            string path = Path.Combine(installDir, PanelManifestFile);
            if (File.Exists(path) && SameWithoutTimestamp(File.ReadAllText(path), json)) return;
            // Written next to it and renamed into place: the panel inside E3D watches this file and would otherwise
            // read it truncated (zero bytes) between WriteAllText's truncate and its write.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            App.Log($"panel manifest: {ex.Message}");
        }
    }

    private static readonly Regex GeneratedAtRe = new(@"""generatedAt"":\s*""[^""]*""", RegexOptions.Compiled);

    /// <summary>Unchanged apart from the time it was written: leave the file alone so open panels do not reload.</summary>
    private static bool SameWithoutTimestamp(string a, string b) => GeneratedAtRe.Replace(a, "") == GeneratedAtRe.Replace(b, "");

    private static readonly JsonSerializerOptions PanelJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── the add-in ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts SEP's panel add-in into the E3D folder and lists it in DesignAddins.xml. A copy loaded by a running
    /// E3D cannot be overwritten: it is renamed aside (allowed for a loaded DLL) and the new one takes its place
    /// for the next start. False when this SEP has no add-in to deploy (the classic ribbon is used then).
    /// </summary>
    private static bool DeployPanelAddin(string installDir)
    {
        byte[]? bytes = PanelAddinBytes.Value;
        if (bytes == null) return false;
        string target = Path.Combine(installDir, PanelAddinName + ".dll");
        try
        {
            foreach (var old in Directory.EnumerateFiles(installDir, PanelAddinName + ".dll.old-*"))
                try { File.Delete(old); } catch { }

            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            {
                string tmp = target + ".new";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(target))
                {
                    try { File.Delete(target); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        File.Move(target, $"{target}.old-{DateTime.Now:yyyyMMddHHmmss}");
                    }
                }
                File.Move(tmp, target);
                App.Log($"panel add-in deployed to {target}");
            }
        }
        catch (Exception ex)
        {
            App.Log($"panel add-in deploy: {ex.Message}");
            if (!File.Exists(target)) return false;
        }

        try
        {
            string xmlPath = Path.Combine(installDir, "DesignAddins.xml");
            if (!File.Exists(xmlPath)) return false;
            string text = File.ReadAllText(xmlPath, Encoding.UTF8);
            if (!text.Contains($"<string>{PanelAddinName}</string>", StringComparison.OrdinalIgnoreCase))
            {
                int closeIdx = text.IndexOf("</ArrayOfString>", StringComparison.OrdinalIgnoreCase);
                if (closeIdx < 0) return false;
                string bak = xmlPath + ".sep.bak";
                if (!File.Exists(bak)) File.Copy(xmlPath, bak);
                text = text.Insert(closeIdx, $"  <string>{PanelAddinName}</string>\r\n");
                File.WriteAllText(xmlPath, text, new UTF8Encoding(false));
            }
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"panel add-in registration: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Puts SEP's ribbon icons next to des.exe. A running E3D keeps the file open: then the copy already there is
    /// used (same icon names) and the new one goes in on a later sync. False when none can be put in place.
    /// </summary>
    private static bool DeployRibbonIcons(string installDir)
    {
        string target = Path.Combine(installDir, IconSetName + ".resources");
        try
        {
            using var s = typeof(PluginService).Assembly.GetManifestResourceStream(IconSetName + ".resources");
            if (s == null) return false;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] bytes = ms.ToArray();
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return true;
            string tmp = target + ".new";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, target, overwrite: true);
            App.Log($"ribbon icons deployed to {target}");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"ribbon icons: {ex.Message}");
            try { File.Delete(target + ".new"); } catch { }
            return File.Exists(target);
        }
    }

    /// <summary>Takes the panel add-in out of E3D again (SEP removed from the PC): its DesignAddins.xml entry, DLL and list.</summary>
    private static void RemovePanelAddin(string installDir)
    {
        try
        {
            string xmlPath = Path.Combine(installDir, "DesignAddins.xml");
            if (File.Exists(xmlPath))
            {
                string text = File.ReadAllText(xmlPath, Encoding.UTF8);
                string cleaned = Regex.Replace(text, $@"\s*<string>{Regex.Escape(PanelAddinName)}</string>", string.Empty, RegexOptions.IgnoreCase);
                if (cleaned != text) File.WriteAllText(xmlPath, cleaned, new UTF8Encoding(false));
            }
            foreach (string f in new[] { PanelAddinName + ".dll", PanelManifestFile, IconSetName + ".resources" })
            {
                string path = Path.Combine(installDir, f);
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { App.Log($"remove {path}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            App.Log($"panel add-in removal: {ex.Message}");
        }
    }

    // ── manifest contract (read by SEP.PluginPanel/Model/Contracts.cs) ──────────

    private sealed class PanelManifestDto
    {
        [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
        [JsonPropertyName("generatedAt")] public string? GeneratedAt { get; set; }
        [JsonPropertyName("sepExe")] public string? SepExe { get; set; }
        [JsonPropertyName("pluginsDir")] public string? PluginsDir { get; set; }
        [JsonPropertyName("plugins")] public List<PanelPluginDto> Plugins { get; set; } = new();
    }

    private sealed class PanelPluginDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("mode")] public string Mode { get; set; } = "buttons";
        [JsonPropertyName("addins")] public List<string> Addins { get; set; } = new();
        [JsonPropertyName("pmlPaths")] public List<string> PmlPaths { get; set; } = new();
        [JsonPropertyName("actions")] public List<PanelActionDto> Actions { get; set; } = new();
    }

    private sealed class PanelActionDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("caption")] public string Caption { get; set; } = string.Empty;
        [JsonPropertyName("tooltip")] public string? Tooltip { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; } = "macro";
        [JsonPropertyName("command")] public string Command { get; set; } = string.Empty;
        [JsonPropertyName("image")] public string? Image { get; set; }
        [JsonPropertyName("group")] public string? Group { get; set; }
        [JsonPropertyName("featured")] public bool Featured { get; set; }
    }
}
