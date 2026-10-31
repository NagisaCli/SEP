using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace SEP.PluginPanel.UI
{
    /// <summary>
    /// The floating bar's window: borderless and transparent, owned by E3D's main window (so it stays above E3D,
    /// minimises with it and never shows in the taskbar) and kept at the same offset from it when E3D moves. It does
    /// not take the keyboard from E3D, except while you type in its search.
    /// </summary>
    public sealed class BarWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        /// <summary>Where a bar that was never moved goes: centred, just under E3D's ribbon.</summary>
        private const double DefaultTop = 150;

        private readonly Forms.Form _owner;
        private readonly PanelViewModel _vm;
        private bool _placing;

        public BarWindow(PanelViewModel vm, Forms.Form owner)
        {
            _vm = vm;
            _owner = owner;
            Title = "SEP plug-in bar";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowInTaskbar = false;
            ShowActivated = false;
            View = new BarView(vm);
            Content = View;

            if (owner != null)
            {
                new WindowInteropHelper(this).Owner = owner.Handle;
                owner.Move += (s, e) => FollowOwner();
                owner.Resize += (s, e) => FollowOwner();
            }
            // a WPF window inside E3D's WinForms message loop needs this for typing (search)
            Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);

            SourceInitialized += (s, e) => SetActivatable(false);
            // a click into E3D (or another program) ends the search
            Deactivated += (s, e) => { if (View.IsSearching) View.CloseSearch(); };
            View.SearchModeChanged += OnSearchMode;
            View.Moved += SaveOffset;
            View.CloseRequested += () => HideBar();
            // turning the bar keeps its corner where it was
            SizeChanged += (s, e) => { if (IsVisible) KeepOnScreen(); };
        }

        public BarView View { get; }

        public void Toggle(bool focusSearch)
        {
            if (IsVisible && !focusSearch) HideBar();
            else ShowBar(focusSearch);
        }

        public void ShowBar(bool focusSearch)
        {
            if (!IsVisible)
            {
                _vm.RefreshStatus();
                Place();
                Show();
                Place();
                View.PlayEnter();
            }
            _vm.IsBarVisible = true;
            if (focusSearch) View.OpenSearch();
        }

        public void HideBar(bool remember = true)
        {
            if (!IsVisible) return;
            if (View.IsSearching) View.CloseSearch();
            if (remember) _vm.IsBarVisible = false;
            View.PlayExit(Hide);
        }

        // ── keyboard: E3D keeps it, except while searching ─────────────────────────

        private void OnSearchMode(bool searching)
        {
            SetActivatable(searching);
            if (searching)
            {
                Activate();
                return;
            }
            // give the keyboard back to E3D (the command you ran may want it: forms, picks, the command line),
            // unless you already went elsewhere
            if (IsActive) try { _owner?.Activate(); } catch { }
        }

        private void SetActivatable(bool activatable)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
            style = activatable ? style & ~WS_EX_NOACTIVATE : style | WS_EX_NOACTIVATE;
            SetWindowLong(hwnd, GWL_EXSTYLE, style);
        }

        // ── where it sits ──────────────────────────────────────────────────────────

        /// <summary>E3D's window in DIPs (the screen's working area without an owner).</summary>
        private Rect OwnerRect()
        {
            if (_owner == null || _owner.IsDisposed || _owner.WindowState == Forms.FormWindowState.Minimized)
            {
                var wa = SystemParameters.WorkArea;
                return wa;
            }
            var b = _owner.Bounds;
            double scale = Scale();
            return new Rect(b.Left / scale, b.Top / scale, b.Width / scale, b.Height / scale);
        }

        private double Scale()
        {
            try
            {
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null) return source.CompositionTarget.TransformToDevice.M11;
                if (_owner != null) return _owner.DeviceDpi / 96.0;
            }
            catch { }
            return 1;
        }

        private Size BarSize()
        {
            if (ActualWidth > 0) return new Size(ActualWidth, ActualHeight);
            View.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return View.DesiredSize;
        }

        private void Place()
        {
            var owner = OwnerRect();
            var size = BarSize();
            Point offset = _vm.BarOffset ?? new Point(Math.Max(0, (owner.Width - size.Width) / 2), DefaultTop);
            MoveTo(owner.Left + offset.X, owner.Top + offset.Y);
            KeepOnScreen();
        }

        private void FollowOwner()
        {
            if (!IsVisible || _owner == null || _owner.WindowState == Forms.FormWindowState.Minimized) return;
            Place();
        }

        /// <summary>At least the grip stays reachable inside E3D's window (or the screen).</summary>
        private void KeepOnScreen()
        {
            var area = OwnerRect();
            var size = BarSize();
            double left = Math.Min(Math.Max(Left, area.Left - size.Width + 80), area.Right - 80);
            double top = Math.Min(Math.Max(Top, area.Top), area.Bottom - 60);
            if (Math.Abs(left - Left) > 0.5 || Math.Abs(top - Top) > 0.5) MoveTo(left, top);
        }

        private void MoveTo(double left, double top)
        {
            _placing = true;
            Left = Math.Round(left);
            Top = Math.Round(top);
            _placing = false;
        }

        private void SaveOffset()
        {
            if (_placing) return;
            KeepOnScreen();
            var owner = OwnerRect();
            _vm.SetBarOffset(new Point(Left - owner.Left, Top - owner.Top));
        }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
