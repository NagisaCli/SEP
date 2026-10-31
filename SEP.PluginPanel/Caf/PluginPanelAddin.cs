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
        private ShowPanelCommand _showCommand;
        private ShowPanelCommand _barCommand;
        private readonly Form _mainForm;
        private int _macroCounter;

        private CafPanel()
        {
            string dir = Path.GetDirectoryName(typeof(CafPanel).Assembly.Location) ?? string.Empty;
            _commands = DependencyResolver.GetImplementationOf<ICommandManager>();
            _runtime = new PanelRuntime(Path.Combine(dir, PanelStore.ManifestFileName), this);

            _container = new Panel { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.FromArgb(0xF3, 0xF5, 0xF8) };
            var windows = DependencyResolver.GetImplementationOf<IWindowManager>();
            _mainForm = windows.MainForm;
            _window = windows.CreateDockedWindow(WindowKey, "Plug-ins", _container, DockedPosition.Right);
            _window.Width = 360;
            // a launcher you keep open: E3D brings it back where you left it
            _window.SaveLayout = true;
            _window.Shown += (s, e) => OnShown();
            _window.Activated += (s, e) => OnShown();
            _window.Closed += (s, e) => { if (_showCommand != null) _showCommand.Checked = false; };

            _showCommand = new ShowPanelCommand(ShowKey, () => TogglePanel(focusSearch: false));
            _commands.Commands.Add(_showCommand);
            _commands.Commands.Add(new ShowPanelCommand(SearchKey, () => TogglePanel(focusSearch: true)));
            _barCommand = new ShowPanelCommand(BarKey, () => ToggleBar(focusSearch: false));
            _commands.Commands.Add(_barCommand);
            _runtime.ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(UI.PanelViewModel.IsBarVisible)) _barCommand.Checked = _runtime.ViewModel.IsBarVisible;
            };
            _commands.Commands.Add(new ShowPanelCommand(BarSearchKey, () => ToggleBar(focusSearch: true)));

            // a layout restored as open shows the window before anyone clicks: build the view on the first idle;
            // the bar comes back once E3D's main window is up, if it was open when E3D last closed
            EventHandler idle = null;
            idle = (s, e) =>
            {
                if (_mainForm != null && !_mainForm.Visible) return;
                Application.Idle -= idle;
                // E3D's own shortcuts are known once its ribbon is up: saved shortcuts that clash with them go
                try { _runtime.ViewModel.RebuildShortcuts(); } catch (Exception ex) { Log.Write("shortcuts: " + ex.Message); }
                try { if (_window.Visible) OnShown(); } catch (Exception ex) { Log.Write("idle: " + ex.Message); }
                try { if (_runtime.ViewModel.IsBarVisible) ShowBar(focusSearch: false); } catch (Exception ex) { Log.Write("bar restore: " + ex); }
            };
            Application.Idle += idle;
        }

        public static void Start()
        {
            if (_instance != null) return;
            _instance = new CafPanel();
            Log.Write("CAF add-in started in " + Path.GetFileName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName));
        }

        public static void Stop()
        {
            _instance?._runtime.Dispose();
            _instance = null;
        }

        private void OnShown()
        {
            _runtime.EnsureView(_container);
            _runtime.Shown();
            if (_showCommand != null) _showCommand.Checked = true;
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
