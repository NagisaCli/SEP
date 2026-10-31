using System;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using SEP.PluginPanel.Model;
using SEP.PluginPanel.UI;

namespace SEP.PluginPanel
{
    /// <summary>
    /// The panel inside a WinForms host (E3D's docked window, or the preview form): store, shortcuts and the view.
    /// Shortcuts and the file watch start at once; the WPF view is only built when the window is first shown, so
    /// E3D starts no slower with the panel closed.
    /// </summary>
    public sealed class PanelRuntime : IDisposable
    {
        private ElementHost _elementHost;
        private PanelView _view;
        private BarWindow _bar;

        public PanelRuntime(string manifestPath, IPanelHost host, string userPath = null)
        {
            Store = new PanelStore(manifestPath, userPath);
            Shortcuts = new ShortcutFilter();
            ViewModel = new PanelViewModel(Store, host, Shortcuts);
            ViewModel.Reload();
            Store.Watch();
            Application.AddMessageFilter(Shortcuts);
            Log.Write($"panel started: manifest {manifestPath} ({(Store.Manifest == null ? "missing" : ViewModel.Cards.Count + " plug-ins")}), layout {Store.UserPath}");
        }

        public PanelStore Store { get; }
        public ShortcutFilter Shortcuts { get; }
        public PanelViewModel ViewModel { get; }
        public PanelView View => _view;
        public BarWindow Bar => _bar;

        /// <summary>The floating bar over <paramref name="owner"/> (E3D's main window), built on first use.</summary>
        public BarWindow EnsureBar(Form owner)
        {
            if (_bar != null) return _bar;
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            _bar = new BarWindow(ViewModel, owner);
            Log.Write("bar created");
            return _bar;
        }

        /// <summary>Builds the WPF view into <paramref name="container"/> (once).</summary>
        public void EnsureView(Control container)
        {
            if (_elementHost != null) return;
            // E3D has no WPF Application object: make sure the pack:// scheme is registered before XAML loads
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            _view = new PanelView(ViewModel);
            _elementHost = new ElementHost { Dock = DockStyle.Fill, Child = _view, BackColor = System.Drawing.Color.FromArgb(0xF3, 0xF5, 0xF8) };
            container.Controls.Add(_elementHost);
            Log.Write("panel view created");
        }

        /// <summary>The window became visible: refresh the status of every plug-in.</summary>
        public void Shown() => ViewModel.RefreshStatus();

        public void FocusSearch()
        {
            if (_elementHost == null) return;
            _elementHost.Focus();
            _view.FocusSearch();
        }

        public void Dispose()
        {
            try { ViewModel.CancelRecording(); ViewModel.SaveNow(); } catch { }
            try { _bar?.Close(); } catch { }
            Application.RemoveMessageFilter(Shortcuts);
            Store.Dispose();
        }
    }
}
