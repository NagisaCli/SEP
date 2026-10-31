using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SEP.PluginPanel.Model
{
    /// <summary>
    /// Reads SEP's manifest and your layout, merges them into cards, saves your changes back (only what differs
    /// from SEP's defaults) and reports when either file changes on disk: SEP rewrote the manifest after a plug-in
    /// change, or the panel of another E3D session saved the layout.
    /// </summary>
    public sealed class PanelStore : IDisposable
    {
        public const string ManifestFileName = "SEP.PluginPanel.json";
        public const string UserFileName = "plugin_panel.user.json";

        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        /// <summary>What this panel last wrote to the layout file: its own save must not reload the panel.</summary>
        private byte[] _ownWrite;

        public PanelStore(string manifestPath, string userPath = null)
        {
            ManifestPath = Path.GetFullPath(manifestPath);
            UserPath = Path.GetFullPath(userPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SEP", UserFileName));
        }

        public string ManifestPath { get; }
        public string UserPath { get; }
        public PanelManifest Manifest { get; private set; }
        public UserLayout User { get; private set; } = new UserLayout();
        public string LoadError { get; private set; }

        /// <summary>Raised on a worker thread, debounced by the caller.</summary>
        public event Action FilesChanged;

        public void Load()
        {
            LoadError = null;
            try { Manifest = Json.Read<PanelManifest>(ManifestPath); }
            catch (Exception ex) { LoadError = "SEP's plug-in list could not be read: " + ex.Message; Log.Write("manifest: " + ex); }
            try { User = Json.Read<UserLayout>(UserPath) ?? new UserLayout(); }
            catch (Exception ex) { User = new UserLayout(); Log.Write("user layout: " + ex); }
        }

        public List<PluginCard> BuildCards()
        {
            var plugins = (Manifest?.Plugins ?? new List<ManifestPlugin>()).Where(p => !string.IsNullOrWhiteSpace(p?.Name)).ToList();
            var order = (User.PluginOrder ?? new List<string>()).Select((n, i) => (n, i))
                .GroupBy(t => t.n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
            var userPlugins = new Dictionary<string, UserPlugin>(User.Plugins ?? new Dictionary<string, UserPlugin>(), StringComparer.OrdinalIgnoreCase);
            var usedShortcuts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string panelShortcut = Shortcut.Normalize(User.PanelShortcut);
            if (panelShortcut != null) usedShortcuts.Add(panelShortcut);

            var cards = new List<PluginCard>();
            foreach (var mp in plugins.Select((p, i) => (p, i)).OrderBy(t => order.TryGetValue(t.p.Name, out int o) ? o : int.MaxValue).ThenBy(t => t.i).Select(t => t.p))
            {
                var card = new PluginCard(mp);
                userPlugins.TryGetValue(mp.Name, out UserPlugin up);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var actions = new List<PanelAction>();
                foreach (var a in mp.Actions ?? new List<ManifestAction>())
                    if (!string.IsNullOrWhiteSpace(a?.Id) && seen.Add(a.Id)) actions.Add(new PanelAction(card, a, isCustom: false));
                foreach (var a in up?.Custom ?? new List<ManifestAction>())
                    if (!string.IsNullOrWhiteSpace(a?.Id) && seen.Add(a.Id)) actions.Add(new PanelAction(card, a, isCustom: true));

                if (up?.ActionOrder != null && up.ActionOrder.Count > 0)
                {
                    var rank = up.ActionOrder.Select((id, i) => (id, i)).GroupBy(t => t.id, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
                    actions = actions.Select((a, i) => (a, i)).OrderBy(t => rank.TryGetValue(t.a.Id, out int r) ? r : int.MaxValue).ThenBy(t => t.i).Select(t => t.a).ToList();
                }

                foreach (var action in actions)
                {
                    if (up?.Actions != null && up.Actions.TryGetValue(action.Id, out UserAction ua) && ua != null)
                    {
                        if (ua.Visible.HasValue) action.IsVisible = ua.Visible.Value;
                        if (!string.IsNullOrWhiteSpace(ua.Caption)) action.Caption = ua.Caption;
                        action.IsPinned = ua.Pinned;
                        string sc = Shortcut.Normalize(ua.Shortcut);
                        if (sc != null && usedShortcuts.Add(sc)) action.Shortcut = sc;
                    }
                    card.Actions.Add(action);
                }

                if (up != null)
                {
                    if (!string.IsNullOrWhiteSpace(up.Mode)) card.Mode = up.Mode;
                    card.IsCollapsed = up.Collapsed;
                }
                card.Refresh();
                cards.Add(card);
            }
            return cards;
        }

        /// <summary>Writes the layout of <paramref name="cards"/>; plug-ins that are not on the panel now (disabled) keep theirs.</summary>
        public void Save(IList<PluginCard> cards, IEnumerable<string> recent, string theme, string panelShortcut, BarLayout bar)
        {
            var plugins = new Dictionary<string, UserPlugin>(User.Plugins ?? new Dictionary<string, UserPlugin>(), StringComparer.OrdinalIgnoreCase);
            foreach (var card in cards)
            {
                var up = new UserPlugin
                {
                    Mode = card.Mode != card.DefaultMode ? card.Mode : null,
                    Collapsed = card.IsCollapsed,
                };
                var defaultOrder = card.Actions.Where(a => !a.IsCustom).Select(a => a.Id).Concat(card.Actions.Where(a => a.IsCustom).Select(a => a.Id)).ToList();
                var currentOrder = card.Actions.Select(a => a.Id).ToList();
                if (!defaultOrder.SequenceEqual(currentOrder)) up.ActionOrder = currentOrder;

                foreach (var a in card.Actions)
                {
                    var ua = new UserAction
                    {
                        Visible = a.IsVisible != a.Featured ? a.IsVisible : (bool?)null,
                        Caption = a.Caption != a.DefaultCaption && !a.IsCustom ? a.Caption : null,
                        Shortcut = a.Shortcut,
                        Pinned = a.IsPinned,
                    };
                    if (ua.Visible.HasValue || ua.Caption != null || ua.Shortcut != null || ua.Pinned)
                        (up.Actions ?? (up.Actions = new Dictionary<string, UserAction>(StringComparer.OrdinalIgnoreCase)))[a.Id] = ua;
                }
                var custom = card.Actions.Where(a => a.IsCustom).Select(a => new ManifestAction
                {
                    Id = a.Id, Caption = a.Caption, Tooltip = a.Tooltip, Kind = a.Kind, Command = a.Command, Image = a.Image, Group = a.Group, Featured = a.Featured,
                }).ToList();
                if (custom.Count > 0) up.Custom = custom;

                bool isDefault = up.Mode == null && !up.Collapsed && up.ActionOrder == null && up.Actions == null && up.Custom == null;
                if (isDefault) plugins.Remove(card.Name);
                else plugins[card.Name] = up;
            }

            var layout = new UserLayout
            {
                Theme = theme,
                PanelShortcut = panelShortcut,
                PluginOrder = cards.Select(c => c.Name).ToList(),
                Plugins = plugins.Count > 0 ? plugins : null,
                Recent = recent?.Take(8).ToList(),
                Bar = bar != null && (bar.Visible || bar.X.HasValue || bar.Vertical) ? bar : null,
            };
            if (layout.Recent != null && layout.Recent.Count == 0) layout.Recent = null;
            // known before the file changes: the watcher may report the write before WriteBytes returns
            byte[] bytes = Json.Serialize(layout);
            _ownWrite = bytes;
            Json.WriteBytes(UserPath, bytes);
            User = layout;
        }

        public void Watch()
        {
            WatchFile(ManifestPath);
            WatchFile(UserPath);
        }

        private void WatchFile(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) return;
                Directory.CreateDirectory(dir);
                var w = new FileSystemWatcher(dir, Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                FileSystemEventHandler changed = (s, e) => OnFileEvent(e.FullPath);
                w.Changed += changed;
                w.Created += changed;
                w.Renamed += (s, e) => OnFileEvent(e.FullPath);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) { Log.Write("watch " + path + ": " + ex.Message); }
        }

        private void OnFileEvent(string path)
        {
            // the watcher's name filter also matches File.Replace's "layout.json~RF….TMP" helper files
            string full;
            try { full = Path.GetFullPath(path); } catch { return; }
            if (!string.Equals(full, UserPath, StringComparison.OrdinalIgnoreCase) && !string.Equals(full, ManifestPath, StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(full, UserPath, StringComparison.OrdinalIgnoreCase))
            {
                // compare content, not time: the watcher can fire before Save has finished
                try
                {
                    var own = _ownWrite;
                    if (own != null && File.ReadAllBytes(UserPath).SequenceEqual(own)) return;
                }
                catch (IOException) { return; }   // still being written: the next event reports it
                catch { }
            }
            Log.Write("changed on disk: " + path);
            FilesChanged?.Invoke();
        }

        public void Dispose()
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }
    }
}
