using System;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using SEP.PluginPanel.Model;
using SEP.PluginPanel.UI;

namespace SEP.PluginPanel
{
    /// <summary>
    /// The panel inside a WinForms host (E3D's docked window): store, shortcuts and the view. The constructor runs
    /// while E3D starts, so it only sets up; the files are read, the plug-ins checked and the shortcuts and file watch
    /// started by <see cref="EnsureLoaded"/> (E3D's first idle, or the first time the panel or bar is shown). The WPF
    /// view is only built when the window is first shown, so E3D starts no slower with the panel closed.
    /// </summary>
    public sealed class PanelRuntime : IDisposable
    {
        private ElementHost _elementHost;
        private PanelView _view;
        private BarWindow _bar;
        private bool _loaded;

        public PanelRuntime(string manifestPath, IPanelHost host, string userPath = null)
        {
            Store = new PanelStore(manifestPath, userPath);
            Shortcuts = new ShortcutFilter();
            ViewModel = new PanelViewModel(Store, host, Shortcuts);
            Application.AddMessageFilter(Shortcuts);
        }

        public PanelStore Store { get; }
        public ShortcutFilter Shortcuts { get; }
        public PanelViewModel ViewModel { get; }

        /// <summary>
        /// Reads SEP's plug-in list and your layout, checks every plug-in and starts the shortcuts and the file watch
        /// (once). True when this call did it.
        /// </summary>
        public bool EnsureLoaded()
        {
            if (_loaded) return false;
            _loaded = true;
            // watch first: a file rewritten while it is being read here is read again
            Store.Watch();
            ViewModel.Reload();
            Log.Write($"panel started: manifest {Store.ManifestPath} ({(Store.Manifest == null ? "missing" : ViewModel.Cards.Count + " plug-ins")}), layout {Store.UserPath}");
            return true;
        }

        /// <summary>The floating bar over <paramref name="owner"/> (E3D's main window), built on first use.</summary>
        public BarWindow EnsureBar(Form owner)
        {
            if (_bar != null) return _bar;
            EnsureLoaded();
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            _bar = new BarWindow(ViewModel, owner);
            Log.Write("bar created");
            return _bar;
        }

        /// <summary>Builds the WPF view into <paramref name="container"/> (once).</summary>
        public void EnsureView(Control container)
        {
            if (_elementHost != null) return;
            EnsureLoaded();
            // E3D has no WPF Application object: make sure the pack:// scheme is registered before XAML loads
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            _view = new PanelView(ViewModel);
            _elementHost = new ElementHost { Dock = DockStyle.Fill, Child = _view, BackColor = System.Drawing.Color.FromArgb(0xF3, 0xF5, 0xF8) };
            container.Controls.Add(_elementHost);
            Log.Write("panel view created");
        }

        /// <summary>The window became visible: refresh the status of every plug-in (unless that was a moment ago).</summary>
        public void Shown() => ViewModel.RefreshStatus();

        public void FocusSearch()
        {
            if (_elementHost == null) return;
            _elementHost.Focus();
            _view.FocusSearch();
        }

        public void Dispose()
        {
            try { ViewModel.Close(); } catch (Exception ex) { Log.Write("close: " + ex.Message); }
            try { _bar?.Close(); } catch { }
            try { _elementHost?.Dispose(); } catch { }
            _bar = null;
            _elementHost = null;
            Application.RemoveMessageFilter(Shortcuts);
            Store.Dispose();
        }
    }
}
