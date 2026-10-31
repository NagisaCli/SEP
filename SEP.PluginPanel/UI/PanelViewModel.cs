using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using SEP.PluginPanel.Model;
using Keys = System.Windows.Forms.Keys;

namespace SEP.PluginPanel.UI
{
    /// <summary>State and behaviour of the panel; the view only forwards clicks and keys.</summary>
    public sealed class PanelViewModel : Observable
    {
        private readonly PanelStore _store;
        private readonly IPanelHost _host;
        private readonly ShortcutFilter _shortcuts;
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _reloadTimer;
        private List<string> _recent = new List<string>();
        private PanelAction _runningAction;

        public PanelViewModel(PanelStore store, IPanelHost host, ShortcutFilter shortcuts)
        {
            _store = store;
            _host = host;
            _shortcuts = shortcuts;
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); SaveNow(); };
            _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _reloadTimer.Tick += (s, e) => { _reloadTimer.Stop(); Reload(); };
            var dispatcher = Dispatcher.CurrentDispatcher;
            _store.FilesChanged += () => dispatcher.BeginInvoke(new Action(() => { _reloadTimer.Stop(); _reloadTimer.Start(); }));
        }

        public ObservableCollection<PluginCard> Cards { get; } = new ObservableCollection<PluginCard>();

        /// <summary>Raised for a short message at the bottom of the panel: (text, ok).</summary>
        public event Action<string, bool> Toast;

        /// <summary>The theme changed (the view swaps its palette).</summary>
        public event Action ThemeChanged;

        // ── loading ────────────────────────────────────────────────────────────────

        public void Reload()
        {
            // what is open stays open: a reload (SEP changed the list, another E3D saved the layout) keeps it
            var expanded = new HashSet<string>(Cards.Where(c => c.ShowMore).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            CancelRecording();
            _store.Load();
            Log.Write("panel reloaded");
            Cards.Clear();
            foreach (var card in _store.BuildCards())
            {
                card.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PluginCard.IsCollapsed) || e.PropertyName == nameof(PluginCard.Mode))
                    {
                        if (e.PropertyName == nameof(PluginCard.Mode)) RaiseLists();
                        ScheduleSave();
                    }
                };
                foreach (var action in card.Actions) Hook(action);
                if (expanded.Contains(card.Name) && card.MoreCount > 0) card.ShowMore = true;
                Cards.Add(card);
            }
            _recent = (_store.User.Recent ?? new List<string>()).ToList();
            _theme = UI.Theme.Normalize(_store.User.Theme);
            _panelShortcut = Shortcut.Normalize(_store.User.PanelShortcut);
            // the bar is where this E3D left it: a layout saved by another session does not move it
            if (_bar == null)
            {
                var saved = _store.User.Bar;
                _bar = new BarLayout { Visible = saved?.Visible ?? false, X = saved?.X, Y = saved?.Y, Vertical = saved?.Vertical ?? false };
            }
            RefreshStatus();
            RebuildShortcuts();
            Raise(nameof(Theme)); Raise(nameof(PanelShortcut)); Raise(nameof(HasPanelShortcut)); Raise(nameof(IsBarVertical));
            Raise(nameof(HasManifest)); Raise(nameof(EmptyTitle)); Raise(nameof(EmptyText)); Raise(nameof(IsEmpty));
            RaiseLists();
            ThemeChanged?.Invoke();
        }

        /// <summary>Edits made straight through bindings (customize mode) re-sort the card and are saved.</summary>
        private void Hook(PanelAction action)
        {
            action.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(PanelAction.IsVisible):
                        action.Plugin.Refresh();
                        ScheduleSave();
                        break;
                    case nameof(PanelAction.IsPinned):
                        RaiseLists();
                        ScheduleSave();
                        break;
                    case nameof(PanelAction.Caption):
                    case nameof(PanelAction.Command):
                        ScheduleSave();
                        break;
                }
            };
        }

        public void RefreshStatus()
        {
            StatusProbe.Evaluate(Cards, _host.HasCommand);
            Raise(nameof(Summary)); Raise(nameof(AttentionCount)); Raise(nameof(ReadyCount)); Raise(nameof(NeedsAttention));
        }

        public bool HasManifest => _store.Manifest != null;
        public bool IsEmpty => Cards.Count == 0;
        public string EmptyTitle => _store.LoadError != null ? "The plug-in list could not be read" : HasManifest ? "No plug-ins enabled" : "SEP has not set up this panel yet";
        public string EmptyText => _store.LoadError ?? (HasManifest
            ? "Enable plug-ins on SEP's Plug-ins page; they appear here (new ones after restarting E3D)."
            : "Open SEP once (SEP ▸ Plug-ins): it lists your enabled plug-ins here and keeps the list up to date.");

        public int ReadyCount => Cards.Count(c => c.State == PluginState.Ready);
        public int AttentionCount => Cards.Count(c => c.State == PluginState.NotLoaded || c.State == PluginState.Missing);
        public bool NeedsAttention => AttentionCount > 0;

        public string Summary
        {
            get
            {
                if (Cards.Count == 0) return "No plug-ins";
                string text = Cards.Count == 1 ? "1 plug-in" : Cards.Count + " plug-ins";
                if (AttentionCount > 0) text += " · " + AttentionCount + " need attention";
                else if (ReadyCount == Cards.Count) text += " · all ready";
                return text;
            }
        }

        // ── lists ──────────────────────────────────────────────────────────────────

        /// <summary>Cards shown in normal use (hidden plug-ins are left out; customize mode shows them all).</summary>
        public IReadOnlyList<PluginCard> ShownCards => IsCustomizing ? Cards.ToList() : Cards.Where(c => !c.IsHidden).ToList();

        public IReadOnlyList<PanelAction> Pinned => AllActions.Where(a => a.IsPinned).ToList();
        public bool HasPinned => Pinned.Count > 0;

        public IReadOnlyList<PanelAction> Recent
        {
            get
            {
                var byKey = AllActions.GroupBy(a => a.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                return _recent.Select(k => byKey.TryGetValue(k, out var a) ? a : null).Where(a => a != null && !a.IsPinned).Take(4).ToList();
            }
        }

        public bool HasRecent => Recent.Count > 0;
        public bool ShowQuickAccess => !IsSearching && !IsCustomizing && (HasPinned || HasRecent);

        private IEnumerable<PanelAction> AllActions => Cards.SelectMany(c => c.Actions);

        /// <summary>Plug-ins on the floating bar: the ones shown with buttons (status-only and hidden ones stay in the panel).</summary>
        public IReadOnlyList<PluginCard> BarPlugins => Cards.Where(c => c.IsButtons && c.HasActions).ToList();

        private void RaiseLists()
        {
            Raise(nameof(BarPlugins)); Raise(nameof(ShownCards)); Raise(nameof(Pinned)); Raise(nameof(HasPinned)); Raise(nameof(Recent)); Raise(nameof(HasRecent));
            Raise(nameof(ShowQuickAccess)); Raise(nameof(SearchResults)); Raise(nameof(HasNoResults)); Raise(nameof(IsEmpty)); Raise(nameof(Summary));
        }

        // ── search ─────────────────────────────────────────────────────────────────

        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (!Set(ref _searchText, value ?? string.Empty)) return;
                Raise(nameof(IsSearching)); Raise(nameof(ShowCards)); RaiseLists();
            }
        }

        public bool IsSearching => !string.IsNullOrWhiteSpace(_searchText);
        public bool ShowCards => !IsSearching;

        /// <summary>Every matching command of every plug-in (also ones not shown as buttons), best first.</summary>
        public IReadOnlyList<PanelAction> SearchResults
        {
            get
            {
                if (!IsSearching) return Array.Empty<PanelAction>();
                string q = _searchText.Trim();
                var tokens = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                return AllActions
                    .Where(a => tokens.All(t => a.SearchText.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderBy(a => a.Caption.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0
                                : a.Caption.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 2)
                    .ThenBy(a => a.IsPinned ? 0 : 1)
                    .ThenBy(a => _recent.IndexOf(a.Key) is int i && i >= 0 ? i : int.MaxValue)
                    .Take(40)
                    .ToList();
            }
        }

        public bool HasNoResults => IsSearching && SearchResults.Count == 0;

        // ── customize mode ─────────────────────────────────────────────────────────

        private bool _isCustomizing;
        public bool IsCustomizing
        {
            get => _isCustomizing;
            set
            {
                if (!Set(ref _isCustomizing, value)) return;
                if (!value) { CancelRecording(); foreach (var c in Cards) c.IsAdding = false; SaveNow(); }
                if (value) SearchText = string.Empty;
                Raise(nameof(IsNormal));
                RaiseLists();
            }
        }

        public bool IsNormal => !_isCustomizing;

        private string _theme = UI.Theme.Light;
        public string Theme
        {
            get => _theme;
            set
            {
                if (!Set(ref _theme, UI.Theme.Normalize(value))) return;
                ThemeChanged?.Invoke();
                ScheduleSave();
            }
        }

        // ── floating bar ───────────────────────────────────────────────────────────

        private BarLayout _bar;

        /// <summary>Saved offset of the bar from E3D's window (DIPs); null until you move it.</summary>
        public Point? BarOffset => _bar?.X != null && _bar.Y != null ? new Point(_bar.X.Value, _bar.Y.Value) : (Point?)null;

        public void SetBarOffset(Point offset)
        {
            _bar.X = Math.Round(offset.X);
            _bar.Y = Math.Round(offset.Y);
            ScheduleSave();
        }

        public bool IsBarVisible
        {
            get => _bar?.Visible ?? false;
            set
            {
                if (_bar == null || _bar.Visible == value) return;
                _bar.Visible = value;
                Raise(nameof(IsBarVisible));
                ScheduleSave();
            }
        }

        public bool IsBarVertical
        {
            get => _bar?.Vertical ?? false;
            set
            {
                if (_bar == null || _bar.Vertical == value) return;
                _bar.Vertical = value;
                Raise(nameof(IsBarVertical));
                ScheduleSave();
            }
        }

        public void ToggleBar(bool focusSearch) => _host.ToggleBar(focusSearch);

        public void ShowPanel() => _host.ShowPanel();

        private string _panelShortcut;
        public string PanelShortcut
        {
            get => _panelShortcut;
            private set { if (Set(ref _panelShortcut, value)) Raise(nameof(HasPanelShortcut)); }
        }

        public bool HasPanelShortcut => !string.IsNullOrEmpty(_panelShortcut);

        private bool _isRecordingPanel;
        public bool IsRecordingPanel { get => _isRecordingPanel; private set => Set(ref _isRecordingPanel, value); }

        // ── running ────────────────────────────────────────────────────────────────

        public void Run(PanelAction action)
        {
            if (action == null) return;
            if (_runningAction != null)
            {
                Toast?.Invoke(_runningAction.Caption + " is still running", false);
                return;
            }
            if (string.IsNullOrWhiteSpace(action.Command))
            {
                Toast?.Invoke(action.Caption + ": no command set", false);
                return;
            }
            // let the click (or key press) finish first: macros often open modal forms or start a 3D pick
            _runningAction = action;
            try
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    try { Execute(action); }
                    finally { _runningAction = null; }
                }));
            }
            catch { _runningAction = null; throw; }
        }

        private void Execute(PanelAction action)
        {
            var elapsed = Stopwatch.StartNew();
            Log.Write($"run start {action.Key} ({action.Kind}: {action.Command})");
            RunResult result;
            if (action.Kind == "command" && !_host.HasCommand(action.Command))
            {
                result = RunResult.Failure("command " + action.Command + " is not registered in this session. Check the plug-in startup log and deployment.");
            }
            else
            {
                try { result = _host.Run(action.Kind, action.Command); }
                catch (Exception ex) { result = RunResult.Failure(ex.Message); }
            }
            Log.Write($"run {action.Key} ({action.Kind}: {action.Command}) -> {(result.Ok ? "ok" : result.Message)} ({elapsed.ElapsedMilliseconds} ms)");
            RefreshStatus();
            Toast?.Invoke(result.Ok ? action.Caption : action.Caption + ": " + result.Message, result.Ok);

            _recent.RemoveAll(k => string.Equals(k, action.Key, StringComparison.OrdinalIgnoreCase));
            _recent.Insert(0, action.Key);
            if (_recent.Count > 8) _recent.RemoveRange(8, _recent.Count - 8);
            Raise(nameof(Recent)); Raise(nameof(HasRecent)); Raise(nameof(ShowQuickAccess));
            ScheduleSave();
        }

        public void RunPml(string line, string what)
        {
            RunResult result;
            try { result = _host.Run("macro", line); }
            catch (Exception ex) { result = RunResult.Failure(ex.Message); }
            Toast?.Invoke(result.Ok ? what : what + ": " + result.Message, result.Ok);
        }

        public void OpenSep()
        {
            string exe = _store.Manifest?.SepExe;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) { Toast?.Invoke("SEP was not found" + (string.IsNullOrWhiteSpace(exe) ? "" : " at " + exe), false); return; }
            try { Process.Start(new ProcessStartInfo(exe, "--page=plugins") { UseShellExecute = true }); }
            catch (Exception ex) { Toast?.Invoke("SEP could not be started: " + ex.Message, false); }
        }

        public void OpenFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) { Toast?.Invoke("Folder not found: " + path, false); return; }
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { Toast?.Invoke(ex.Message, false); }
        }

        public string PluginsDir => _store.Manifest?.PluginsDir;

        // ── editing ────────────────────────────────────────────────────────────────

        public void TogglePin(PanelAction action) => action.IsPinned = !action.IsPinned;

        public void SetButton(PanelAction action, bool asButton) => action.IsVisible = asButton;

        public void MoveAction(PanelAction action, int delta)
        {
            var list = action.Plugin.Actions;
            int i = list.IndexOf(action), j = i + delta;
            if (i < 0 || j < 0 || j >= list.Count) return;
            list.Move(i, j);
            action.Plugin.Refresh();
            ScheduleSave();
        }

        public void MovePlugin(PluginCard card, int delta)
        {
            int i = Cards.IndexOf(card), j = i + delta;
            if (i < 0 || j < 0 || j >= Cards.Count) return;
            Cards.Move(i, j);
            RaiseLists();
            ScheduleSave();
        }

        public void AddCustom(PluginCard card, string caption, string command)
        {
            if (string.IsNullOrWhiteSpace(caption) || string.IsNullOrWhiteSpace(command)) { Toast?.Invoke("Enter a name and a PML command", false); return; }
            var source = new ManifestAction
            {
                Id = "custom." + Guid.NewGuid().ToString("N").Substring(0, 8),
                Caption = caption.Trim(),
                Command = command.Trim(),
                Kind = "macro",
                Featured = true,
                Tooltip = "Your command",
            };
            var action = new PanelAction(card, source, isCustom: true);
            Hook(action);
            card.Actions.Add(action);
            card.IsAdding = false;
            card.Refresh();
            ScheduleSave();
        }

        public void DeleteCustom(PanelAction action)
        {
            if (!action.IsCustom) return;
            action.Plugin.Actions.Remove(action);
            action.Plugin.Refresh();
            RebuildShortcuts();
            RaiseLists();
            ScheduleSave();
        }

        public void ResetPlugin(PluginCard card)
        {
            card.Mode = card.DefaultMode;
            foreach (var a in card.Actions.ToList())
            {
                if (a.IsCustom) continue;
                a.IsVisible = a.Featured;
                a.Caption = a.DefaultCaption;
            }
            var order = card.Actions.Where(a => !a.IsCustom).Concat(card.Actions.Where(a => a.IsCustom)).ToList();
            for (int i = 0; i < order.Count; i++) card.Actions.Move(card.Actions.IndexOf(order[i]), i);
            card.Refresh();
            ScheduleSave();
        }

        // ── shortcuts ──────────────────────────────────────────────────────────────

        private PanelAction _recording;

        public void RecordShortcut(PanelAction action)
        {
            CancelRecording();
            _recording = action;
            action.IsRecording = true;
            _shortcuts.Record(chord => FinishRecording(action, chord));
        }

        public void RecordPanelShortcut()
        {
            CancelRecording();
            IsRecordingPanel = true;
            _shortcuts.Record(chord => FinishRecording(null, chord));
        }

        public void CancelRecording()
        {
            _shortcuts.StopRecording();
            if (_recording != null) _recording.IsRecording = false;
            _recording = null;
            IsRecordingPanel = false;
        }

        public void ClearShortcut(PanelAction action)
        {
            action.Shortcut = null;
            RebuildShortcuts();
            ScheduleSave();
        }

        public void ClearPanelShortcut()
        {
            PanelShortcut = null;
            RebuildShortcuts();
            ScheduleSave();
        }

        private void FinishRecording(PanelAction action, Keys chord)
        {
            if (action != null) action.IsRecording = false;
            IsRecordingPanel = false;
            _recording = null;
            Keys key = chord & Keys.KeyCode;
            if (key == Keys.Escape) return;
            string text = null;
            if (key == Keys.Back || key == Keys.Delete)
            {
                if ((chord & Shortcut.ModifierMask) != 0) text = Shortcut.IsAssignable(chord) ? Shortcut.Format(chord) : null;
                if (text == null) { Assign(action, null); return; }
            }
            if (text == null)
            {
                if (!Shortcut.IsAssignable(chord))
                {
                    Toast?.Invoke("Use Ctrl or Alt with a key, or a function key (" + Shortcut.Format(chord) + " is for typing)", false);
                    return;
                }
                text = Shortcut.Format(chord);
            }
            string taken = TakenBy(chord, E3dShortcuts());
            if (taken != null)
            {
                Toast?.Invoke(text + " is " + taken + ". Choose other keys.", false);
                return;
            }

            // one shortcut, one target: take it from whoever had it
            string previous = null;
            if (string.Equals(PanelShortcut, text, StringComparison.OrdinalIgnoreCase) && action != null) { PanelShortcut = null; previous = "opening the panel"; }
            foreach (var other in AllActions.Where(a => a != action && string.Equals(a.Shortcut, text, StringComparison.OrdinalIgnoreCase)))
            {
                other.Shortcut = null;
                previous = other.Caption;
            }
            Assign(action, text);
            Toast?.Invoke(text + " → " + (action?.Caption ?? "open the panel") + (previous != null ? " (was " + previous + ")" : ""), true);
        }

        private void Assign(PanelAction action, string text)
        {
            if (action != null) action.Shortcut = text;
            else PanelShortcut = text;
            RebuildShortcuts();
            ScheduleSave();
        }

        public void RebuildShortcuts()
        {
            // a saved shortcut E3D or Windows uses (E3D's are only known once its ribbon is loaded) is dropped, so
            // the keys keep doing what E3D does
            var e3d = E3dShortcuts();
            var dropped = new List<string>();
            string Free(string shortcut, string target)
            {
                string taken = shortcut == null ? null : TakenBy(Shortcut.Parse(shortcut), e3d);
                if (taken == null) return shortcut;
                dropped.Add(shortcut + " (" + target + ") is " + taken);
                return null;
            }
            PanelShortcut = Free(PanelShortcut, "open the panel");
            foreach (var a in AllActions.Where(x => x.HasShortcut).ToList()) a.Shortcut = Free(a.Shortcut, a.Caption);
            if (dropped.Count > 0)
            {
                Log.Write("shortcuts removed: " + string.Join("; ", dropped));
                Toast?.Invoke("Shortcut removed: " + string.Join("; ", dropped), false);
                ScheduleSave();
            }

            var map = new Dictionary<Keys, Action>();
            Keys panel = Shortcut.Parse(PanelShortcut);
            if (panel != Keys.None) map[panel] = () => _host.ToggleBar(focusSearch: true);
            foreach (var a in AllActions.Where(x => x.HasShortcut))
            {
                Keys chord = Shortcut.Parse(a.Shortcut);
                var target = a;
                if (chord != Keys.None && !map.ContainsKey(chord)) map[chord] = () => Run(target);
            }
            _shortcuts.SetMap(map);
        }

        /// <summary>Who already answers to <paramref name="chord"/> ("used by E3D (Save Work)"); null when it is free.</summary>
        private static string TakenBy(Keys chord, IReadOnlyDictionary<Keys, string> e3d)
        {
            if (e3d != null && e3d.TryGetValue(chord, out string command)) return "used by E3D (" + command + ")";
            string windows = Shortcut.ReservedFor(chord);
            return windows != null ? "used by Windows (" + windows + ")" : null;
        }

        private IReadOnlyDictionary<Keys, string> E3dShortcuts()
        {
            try { return _host.E3dShortcuts(); }
            catch (Exception ex) { Log.Write("E3D shortcuts: " + ex.Message); return null; }
        }

        // ── saving ─────────────────────────────────────────────────────────────────

        public void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public void SaveNow()
        {
            _saveTimer.Stop();
            try { _store.Save(Cards, _recent, _theme, _panelShortcut, _bar); }
            catch (Exception ex)
            {
                Log.Write("save: " + ex);
                Toast?.Invoke("Your layout could not be saved: " + ex.Message, false);
            }
        }
    }
}
