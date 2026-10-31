using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SEP.PluginPanel.Model;
using Shortcut = SEP.PluginPanel.Model.Shortcut;

namespace SEP.PluginPanel
{
    /// <summary>%LOCALAPPDATA%\SEP\plugin_panel.log, next to SEP's own log. Never throws.</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        public static string FilePath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SEP", "plugin_panel.log");

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 512 * 1024) File.Delete(FilePath);
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{System.Diagnostics.Process.GetCurrentProcess().Id}] {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }

    public sealed class RunResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public static RunResult Success(string message = null) => new RunResult { Ok = true, Message = message };
        public static RunResult Failure(string message) => new RunResult { Ok = false, Message = message };
    }

    /// <summary>What the panel needs from E3D: running commands. The CAF add-in implements it; the preview fakes it.</summary>
    public interface IPanelHost
    {
        /// <summary>Runs a CAF command (by key) or a PML command line. Called on the UI thread.</summary>
        RunResult Run(string kind, string command);

        /// <summary>Whether a CAF command with this key exists in this session.</summary>
        bool HasCommand(string key);

        /// <summary>Shows or hides the docked panel (where plug-ins are managed).</summary>
        void TogglePanel(bool focusSearch);

        /// <summary>Shows the docked panel (it stays open when it already is).</summary>
        void ShowPanel();

        /// <summary>Shows or hides the floating bar; with <paramref name="focusSearch"/> it opens on its search.</summary>
        void ToggleBar(bool focusSearch);

        /// <summary>
        /// The keyboard shortcuts of E3D's own commands in this session (chord → command name). Empty until E3D has
        /// loaded its ribbon. Called on the UI thread.
        /// </summary>
        IReadOnlyDictionary<Keys, string> E3dShortcuts();
    }

    /// <summary>Works out a plug-in's state in this E3D session from what the process has loaded.</summary>
    public static class StatusProbe
    {
        public static void Evaluate(IEnumerable<PluginCard> cards, Func<string, bool> hasCommand = null)
        {
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { loaded.Add(asm.GetName().Name); } catch { }
                }
            }
            catch { }
            // PMLLIB may separate its folders with ';' or spaces, and folders may contain spaces: match whole folders
            string pmllib = ";" + NormalizeDir(Environment.GetEnvironmentVariable("PMLLIB")).ToLowerInvariant() + ";";
            foreach (var card in cards) Evaluate(card, loaded, pmllib, hasCommand);
        }

        private static bool OnPmlLib(string pmllib, string dir)
        {
            string d = NormalizeDir(dir).ToLowerInvariant();
            if (d.Length == 0) return true;
            for (int i = pmllib.IndexOf(d, StringComparison.Ordinal); i >= 0; i = pmllib.IndexOf(d, i + 1, StringComparison.Ordinal))
            {
                char before = pmllib[i - 1], after = i + d.Length < pmllib.Length ? pmllib[i + d.Length] : ';';
                if (IsBoundary(before) && (IsBoundary(after) || after == '\\' &&
                    (i + d.Length + 1 == pmllib.Length || IsBoundary(pmllib[i + d.Length + 1])))) return true;
            }
            return false;
        }

        private static bool IsBoundary(char c) => c == ';' || c == '"' || c == '(' || c == ')' || char.IsWhiteSpace(c);

        private static void Evaluate(PluginCard card, ISet<string> loaded, string pmllib, Func<string, bool> hasCommand)
        {
            if (!string.IsNullOrEmpty(card.Path) && !Directory.Exists(card.Path))
            {
                card.SetState(PluginState.Missing, "Folder missing", "SEP lists this plug-in but its folder is gone:\n" + card.Path);
                return;
            }
            var missingAddins = card.Addins.Where(a => !loaded.Contains(a)).ToList();
            var missingPml = card.PmlPaths.Where(p => !OnPmlLib(pmllib, p)).ToList();
            var commands = card.Actions.Where(a => a.Kind == "command").Select(a => a.Command)
                .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var missingCommands = hasCommand == null ? new List<string>() : commands.Where(c => !hasCommand(c)).ToList();
            // Registered commands are stronger evidence than a package's assembly filename.
            if (hasCommand != null && commands.Count > 0 && missingCommands.Count == 0) missingAddins.Clear();
            if (card.Addins.Count == 0 && card.PmlPaths.Count == 0 && commands.Count == 0)
            {
                card.SetState(PluginState.Enabled, "Enabled", "Enabled in SEP.");
                return;
            }
            if (missingAddins.Count == 0 && missingPml.Count == 0 && missingCommands.Count == 0)
            {
                var parts = new List<string>();
                if (card.Addins.Count > 0) parts.Add("Add-in loaded: " + string.Join(", ", card.Addins));
                if (card.PmlPaths.Count > 0) parts.Add("PML on PMLLIB: " + string.Join(", ", card.PmlPaths));
                card.SetState(PluginState.Ready, card.Addins.Count > 0 ? "Running" : "Ready", string.Join("\n", parts));
                return;
            }
            var why = new List<string>();
            if (missingAddins.Count > 0) why.Add("Add-in not loaded: " + string.Join(", ", missingAddins));
            if (missingPml.Count > 0) why.Add("Not on this session's PMLLIB: " + string.Join(", ", missingPml));
            if (missingCommands.Count > 0) why.Add("Commands not registered: " + string.Join(", ", missingCommands));
            why.Add("If enabled during this session, open a new E3D session. If this persists, check the plug-in deployment and startup log.");
            card.SetState(PluginState.NotLoaded, "Not loaded", string.Join("\n", why));
        }

        private static string NormalizeDir(string path) => (path ?? string.Empty).Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
    }

    /// <summary>
    /// The panel's keyboard shortcuts, active anywhere in E3D (also while the panel is closed). A message filter on
    /// E3D's UI thread sees each key press before E3D does; only chords you assigned are taken, everything else
    /// passes through. While a shortcut is being recorded the next chord goes to the recorder instead.
    /// </summary>
    public sealed class ShortcutFilter : IMessageFilter
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private Dictionary<Keys, Action> _map = new Dictionary<Keys, Action>();
        private Action<Keys> _recorder;

        public void SetMap(Dictionary<Keys, Action> map) => _map = map ?? new Dictionary<Keys, Action>();

        /// <summary>The next key chord goes to <paramref name="recorder"/> (Escape and Backspace included).</summary>
        public void Record(Action<Keys> recorder) => _recorder = recorder;

        public void StopRecording() => _recorder = null;

        public bool IsRecording => _recorder != null;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_KEYDOWN && m.Msg != WM_SYSKEYDOWN) return false;
            Keys key = (Keys)(int)m.WParam & Keys.KeyCode;
            if (Shortcut.IsModifierKey(key)) return false;
            Keys chord = key | (Control.ModifierKeys & Shortcut.ModifierMask);

            if (_recorder != null)
            {
                var recorder = _recorder;
                _recorder = null;
                Post(() => recorder(chord));
                return true;
            }
            if (!_map.TryGetValue(chord, out Action action)) return false;
            // run after the key press is done: commands may open forms or take focus
            Post(action);
            return true;
        }

        private static void Post(Action action)
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(() =>
            {
                try { action(); }
                catch (Exception ex) { Log.Write("shortcut: " + ex); }
            }));
        }
    }
}
