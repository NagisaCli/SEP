// CAF entry point ("SEP.PluginPanel" in DesignAddins.xml, deployed by SEP). Same shape as the add-ins that run on
// this E3D 3.1 today (E3DHUB, EGIT): IAddinInjected, work in Start(IDependencyResolver), a docked window from
// IWindowManager and commands added to ICommandManager under the key the ribbon button names.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Aveva.ApplicationFramework;
using Aveva.ApplicationFramework.Presentation;
using SEP.PluginPanel.Model;
using PmlCommand = Aveva.Core.Utilities.CommandLine.Command;

namespace SEP.PluginPanel.Caf
{
    public sealed class PluginPanelAddin : IAddinInjected
    {
        public string Name => "SEP.PluginPanel";
        public string Description => "SEP plug-in panel: find, run and manage the plug-ins SEP enabled.";

        public void Start(IDependencyResolver resolver)
        {
            try { CafPanel.Start(); }
            catch (Exception ex) { Log.Write("start failed: " + ex); }
        }

        // kept for the inherited IAddin contract; E3D 3.1 calls Start(IDependencyResolver)
        public void Start(ServiceManager serviceManager) { }

        public void Stop()
        {
            try { CafPanel.Stop(); }
            catch (Exception ex) { Log.Write("stop failed: " + ex); }
        }
    }

    internal sealed class CafPanel : IPanelHost
    {
        public const string WindowKey = "SEP.PluginPanel";
        public const string ShowKey = "SEP.PluginPanel.Show";
        public const string SearchKey = "SEP.PluginPanel.Search";
        public const string BarKey = "SEP.PluginBar.Toggle";
        public const string BarSearchKey = "SEP.PluginBar.Search";

        private static CafPanel _instance;
        private readonly PanelRuntime _runtime;
        private readonly DockedWindow _window;
        private readonly Panel _container;
        private readonly ICommandManager _commands;
        /// <summary>The commands this panel added to CAF, taken out again by <see cref="Teardown"/>.</summary>
        private readonly List<Command> _added = new List<Command>();
        private ShowPanelCommand _showCommand;
        private ShowPanelCommand _barCommand;
        private readonly Form _mainForm;
        private EventHandler _idle;
        private bool _stopped;
        private int _macroCounter;

        private CafPanel()
        {
            string dir = Path.GetDirectoryName(typeof(CafPanel).Assembly.Location) ?? string.Empty;
            _commands = DependencyResolver.GetImplementationOf<ICommandManager>();
            // only sets up: the files are read and the plug-ins checked on E3D's first idle (below)
            _runtime = new PanelRuntime(Path.Combine(dir, PanelStore.ManifestFileName), this);
            try
            {
                _container = new Panel { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.FromArgb(0xF3, 0xF5, 0xF8) };
                var windows = DependencyResolver.GetImplementationOf<IWindowManager>();
                _mainForm = windows.MainForm;
                _window = windows.CreateDockedWindow(WindowKey, "Plug-ins", _container, DockedPosition.Right);
                _window.Width = 360;
                // a launcher you keep open: E3D brings it back where you left it
                _window.SaveLayout = true;
                // Activated comes with every click into the panel: the status check behind it is skipped when the last
                // one was a moment ago
                _window.Shown += (s, e) => OnWindowShown();
                _window.Activated += (s, e) => OnWindowShown();
                _window.Closed += (s, e) => OnWindowClosed();

                _showCommand = AddCommand(ShowKey, () => TogglePanel(focusSearch: false));
                AddCommand(SearchKey, () => TogglePanel(focusSearch: true));
                _barCommand = AddCommand(BarKey, () => ToggleBar(focusSearch: false));
                _runtime.ViewModel.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(UI.PanelViewModel.IsBarVisible)) _barCommand.Checked = _runtime.ViewModel.IsBarVisible;
                };
                AddCommand(BarSearchKey, () => ToggleBar(focusSearch: true));

                // once E3D's main window is up: read the plug-in list and your layout and check the plug-ins (not
                // during E3D's start); a layout restored as open shows the window before anyone clicks: build the view;
                // the bar comes back if it was open when E3D last closed
                _idle = (s, e) =>
                {
                    if (_mainForm != null && !_mainForm.Visible) return;
                    Application.Idle -= _idle;
                    _idle = null;
                    // E3D's own shortcuts are known once its ribbon is up: saved shortcuts that clash with them go. A
                    // window shown earlier already loaded the panel, before the ribbon: check the shortcuts again
                    try { if (!_runtime.EnsureLoaded()) _runtime.ViewModel.RebuildShortcuts(); } catch (Exception ex) { Log.Write("load: " + ex); }
                    try { if (_window.Visible) OnShown(); } catch (Exception ex) { Log.Write("idle: " + ex.Message); }
                    try { if (_runtime.ViewModel.IsBarVisible) ShowBar(focusSearch: false); } catch (Exception ex) { Log.Write("bar restore: " + ex); }
                };
                Application.Idle += _idle;
            }
            catch
            {
                // a half-built panel must not stay: its shortcut filter would take keys for the rest of the session
                Teardown();
                throw;
            }
        }

        public static void Start()
        {
            if (_instance != null) return;
            _instance = new CafPanel();
            Log.Write("CAF add-in started in " + Path.GetFileName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName));
        }

        public static void Stop()
        {
            var instance = _instance;
            _instance = null;
            instance?.Teardown();
        }

        private ShowPanelCommand AddCommand(string key, Action execute)
        {
            // a command left behind (if it cannot be removed) does nothing once the panel is gone
            var command = new ShowPanelCommand(key, () => { if (!_stopped) execute(); });
            _commands.Commands.Add(command);
            _added.Add(command);
            return command;
        }

        /// <summary>Undoes what the constructor set up, also when it stopped half-way. Never throws.</summary>
        private void Teardown()
        {
            _stopped = true;
            if (_idle != null) Application.Idle -= _idle;
            _idle = null;
            // saves your layout, closes the bar, drops the view, the shortcut filter and the file watch
            try { _runtime.Dispose(); } catch (Exception ex) { Log.Write("dispose: " + ex); }
            foreach (var command in _added) RemoveCommand(command);
            _added.Clear();
        }

        /// <summary>
        /// Takes a command out of CAF's list. The method is looked up at run time (by command, else by key), so the
        /// add-in still loads on an E3D whose command list has no such method; the command then just stays inert.
        /// </summary>
        private void RemoveCommand(Command command)
        {
            try
            {
                object list = _commands.Commands;
                var byCommand = list.GetType().GetMethod("Remove", new[] { typeof(Command) });
                if (byCommand != null) { byCommand.Invoke(list, new object[] { command }); return; }
                var byKey = list.GetType().GetMethod("Remove", new[] { typeof(string) });
                if (byKey != null) { byKey.Invoke(list, new object[] { command.Key }); return; }
                Log.Write("command " + command.Key + " left registered: the command list has no Remove");
            }
            catch (Exception ex) { Log.Write("remove command " + command.Key + ": " + ex.Message); }
        }

        private void OnShown()
        {
            _runtime.EnsureView(_container);
            _runtime.Shown();
            if (_showCommand != null) _showCommand.Checked = true;
        }

        // CAF raises these from E3D's message loop: an exception must not get past here
        private void OnWindowShown()
        {
            if (_stopped) return;
            try { OnShown(); }
            catch (Exception ex) { Log.Write("shown: " + ex); }
        }

        private void OnWindowClosed()
        {
            if (_stopped) return;
            try { if (_showCommand != null) _showCommand.Checked = false; }
            catch (Exception ex) { Log.Write("closed: " + ex.Message); }
        }

        // ── IPanelHost ─────────────────────────────────────────────────────────────

        public void TogglePanel(bool focusSearch)
        {
            if (_window.Visible && !focusSearch)
            {
                _window.Hide();
                _showCommand.Checked = false;
                return;
            }
            _window.Show();
            try { _window.Activate(); } catch { }
            OnShown();
            if (focusSearch) _runtime.FocusSearch();
        }

        public void ShowPanel()
        {
            _window.Show();
            try { _window.Activate(); } catch { }
            OnShown();
        }

        public void ToggleBar(bool focusSearch)
        {
            _runtime.EnsureBar(_mainForm).Toggle(focusSearch);
        }

        private void ShowBar(bool focusSearch) => _runtime.EnsureBar(_mainForm).ShowBar(focusSearch);

        // every ribbon/menu tool E3D loaded from its .uic files, with the shortcut it answers to (Ctrl+S, F8, Ins…)
        public IReadOnlyDictionary<Keys, string> E3dShortcuts()
        {
            var result = new Dictionary<Keys, string>();
            var bars = DependencyResolver.GetImplementationOf<IWindowManager>()?.CommandBarManager;
            if (bars?.RootTools == null) return result;
            foreach (object item in bars.RootTools)
            {
                if (!(item is ITool tool)) continue;
                var chord = (Keys)(int)tool.Shortcut;
                if (chord == Keys.None || result.ContainsKey(chord)) continue;
                string caption = (tool.Caption ?? string.Empty).Replace("&", string.Empty).Trim();
                result[chord] = caption.Length > 0 ? caption : tool.Key;
            }
            return result;
        }

        public bool HasCommand(string key)
        {
            try { return !string.IsNullOrWhiteSpace(key) && _commands.Commands.ContainsKey(key); }
            catch { return false; }
        }

        public RunResult Run(string kind, string command)
        {
            if (kind == "command")
            {
                return CafCommandRunner.Run(_commands.Commands[command]);
            }

            var lines = (command ?? string.Empty).Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count == 0) return RunResult.Failure("empty command");
            if (lines.Count == 1) return RunPml(lines[0]);
            // several lines (or PML2 the command line refuses): run them as a macro
            string dir = Path.Combine(Path.GetTempPath(), "SEP");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"panel-{System.Diagnostics.Process.GetCurrentProcess().Id}-{++_macroCounter % 20}.mac");
            File.WriteAllLines(file, lines, new UTF8Encoding(false));
            return RunPml("$M \"" + file.Replace('\\', '/') + "\"");
        }

        // exactly what E3D's own ribbon does for a <Type>Macro</Type> button (Startup.dll PMLMacroCommand):
        // Run() parses in a fresh scope and fails on macros that work from the ribbon ("CP: Syntax error",
        // PML.NET "Item has already been added" when an object file re-imports its assembly)
        private static RunResult RunPml(string line)
        {
            PmlCommand cmd = PmlCommand.CreateCommand(line);
            if (cmd.RunInCurrentScope()) return RunResult.Success();
            string error = null;
            try { error = cmd.Error == null ? null : $"({cmd.Error.ModuleNumber},{cmd.Error.MessageNumber}) {cmd.Error.MessageText()}"; } catch { }
            return RunResult.Failure(string.IsNullOrWhiteSpace(error) ? "E3D refused the command" : error.Trim());
        }
    }

    /// <summary>Ribbon button command: shows or hides the panel (Checked while it is open).</summary>
    internal sealed class ShowPanelCommand : Command
    {
        private readonly Action _execute;

        internal ShowPanelCommand(string key, Action execute)
        {
            Key = key;
            ExecuteOnCheckedChange = false;
            _execute = execute;
        }

        public override void Execute()
        {
            try { _execute(); }
            catch (Exception ex) { Log.Write(Key + ": " + ex); }
        }
    }
}
