using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SEP.PluginPanel.Model
{
    /// <summary>
    /// What SEP wants the panel to show (SEP.PluginPanel.json next to this DLL, written by SEP.App's
    /// PluginPanelCatalog on every plug-in change). Read-only for the panel.
    /// </summary>
    [DataContract]
    public sealed class PanelManifest
    {
        [DataMember(Name = "schema")] public int Schema { get; set; }
        [DataMember(Name = "generatedAt")] public string GeneratedAt { get; set; }
        [DataMember(Name = "sepExe")] public string SepExe { get; set; }
        [DataMember(Name = "pluginsDir")] public string PluginsDir { get; set; }
        [DataMember(Name = "plugins")] public List<ManifestPlugin> Plugins { get; set; }
    }

    [DataContract]
    public sealed class ManifestPlugin
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "displayName")] public string DisplayName { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        [DataMember(Name = "path")] public string Path { get; set; }
        /// <summary>"buttons" (commands as buttons) or "status" (no buttons: a status line, commands in the menu).</summary>
        [DataMember(Name = "mode")] public string Mode { get; set; }
        /// <summary>CAF add-ins of the plug-in (assembly names); loaded = running.</summary>
        [DataMember(Name = "addins")] public List<string> Addins { get; set; }
        /// <summary>Folders SEP put on PMLLIB for it; on this session's PMLLIB = ready.</summary>
        [DataMember(Name = "pmlPaths")] public List<string> PmlPaths { get; set; }
        [DataMember(Name = "actions")] public List<ManifestAction> Actions { get; set; }
    }

    [DataContract]
    public sealed class ManifestAction
    {
        /// <summary>Unique within the plug-in: the tool name of its .uic, or SEP's generated key.</summary>
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "caption")] public string Caption { get; set; }
        [DataMember(Name = "tooltip")] public string Tooltip { get; set; }
        /// <summary>"command" (a CAF command key) or "macro" (a PML command line).</summary>
        [DataMember(Name = "kind")] public string Kind { get; set; }
        [DataMember(Name = "command")] public string Command { get; set; }
        /// <summary>The .uic ImageResourceId (e.g. AvevaSharedIcons:ID_EYE); only used to pick a glyph.</summary>
        [DataMember(Name = "image")] public string Image { get; set; }
        [DataMember(Name = "group")] public string Group { get; set; }
        /// <summary>Shown as a button until you choose otherwise (the plug-in's large ribbon buttons).</summary>
        [DataMember(Name = "featured")] public bool Featured { get; set; }
    }

    /// <summary>Your layout of the panel (%APPDATA%\SEP\plugin_panel.user.json). Only differences from SEP's defaults are kept.</summary>
    [DataContract]
    public sealed class UserLayout
    {
        [DataMember(Name = "schema")] public int Schema { get; set; } = 1;
        [DataMember(Name = "theme", EmitDefaultValue = false)] public string Theme { get; set; }
        [DataMember(Name = "panelShortcut", EmitDefaultValue = false)] public string PanelShortcut { get; set; }
        [DataMember(Name = "pluginOrder", EmitDefaultValue = false)] public List<string> PluginOrder { get; set; }
        [DataMember(Name = "plugins", EmitDefaultValue = false)] public Dictionary<string, UserPlugin> Plugins { get; set; }
        /// <summary>"plugin/actionId", most recent first.</summary>
        [DataMember(Name = "recent", EmitDefaultValue = false)] public List<string> Recent { get; set; }
        [DataMember(Name = "bar", EmitDefaultValue = false)] public BarLayout Bar { get; set; }
    }

    /// <summary>The floating bar: open or not, where (offset from E3D's window, in DIPs) and which way it runs.</summary>
    [DataContract]
    public sealed class BarLayout
    {
        [DataMember(Name = "visible", EmitDefaultValue = false)] public bool Visible { get; set; }
        [DataMember(Name = "x", EmitDefaultValue = false)] public double? X { get; set; }
        [DataMember(Name = "y", EmitDefaultValue = false)] public double? Y { get; set; }
        [DataMember(Name = "vertical", EmitDefaultValue = false)] public bool Vertical { get; set; }
    }

    [DataContract]
    public sealed class UserPlugin
    {
        [DataMember(Name = "mode", EmitDefaultValue = false)] public string Mode { get; set; }
        [DataMember(Name = "collapsed", EmitDefaultValue = false)] public bool Collapsed { get; set; }
        [DataMember(Name = "actionOrder", EmitDefaultValue = false)] public List<string> ActionOrder { get; set; }
        [DataMember(Name = "actions", EmitDefaultValue = false)] public Dictionary<string, UserAction> Actions { get; set; }
        [DataMember(Name = "custom", EmitDefaultValue = false)] public List<ManifestAction> Custom { get; set; }
    }

    [DataContract]
    public sealed class UserAction
    {
        [DataMember(Name = "visible", EmitDefaultValue = false)] public bool? Visible { get; set; }
        [DataMember(Name = "caption", EmitDefaultValue = false)] public string Caption { get; set; }
        [DataMember(Name = "shortcut", EmitDefaultValue = false)] public string Shortcut { get; set; }
        [DataMember(Name = "pinned", EmitDefaultValue = false)] public bool Pinned { get; set; }
    }

    internal static class Json
    {
        private static DataContractJsonSerializer Serializer<T>() =>
            new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        public static T Read<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            // SEP writes UTF-8 without BOM; a file being rewritten right now reads as empty or truncated
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return null;
            using (var ms = new MemoryStream(bytes))
                return (T)Serializer<T>().ReadObject(ms);
        }

        public static byte[] Serialize<T>(T value)
        {
            using (var ms = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    Serializer<T>().WriteObject(writer, value);
                    writer.Flush();
                }
                return ms.ToArray();
            }
        }

        /// <summary>Replaces the file in one step (a reader never sees half a file).</summary>
        public static void WriteBytes(string path, byte[] bytes)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
