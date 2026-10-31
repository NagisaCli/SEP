using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SEP.PluginPanel.Model;

namespace SEP.PluginPanel.UI
{
    public partial class PanelView : UserControl
    {
        private readonly PanelViewModel _vm;
        private ResourceDictionary _palette;
        private readonly DispatcherTimer _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        private int _selected = -1;

        public PanelView(PanelViewModel vm)
        {
            _vm = vm;
            ApplyTheme();
            InitializeComponent();
            DataContext = vm;
            vm.ThemeChanged += ApplyTheme;
            vm.Toast += ShowToast;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(PanelViewModel.SearchResults)) _selected = -1;
            };
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); HideToast(); };
            SmoothScroll.Attach(Scroller);
        }

        public PanelViewModel ViewModel => _vm;

        /// <summary>Puts the caret in the search box (the panel's own shortcut, the ribbon's search command).</summary>
        public void FocusSearch()
        {
            if (_vm.IsCustomizing) _vm.IsCustomizing = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                Search.Focus();
                Keyboard.Focus(Search);
                Search.SelectAll();
            }));
        }

        private void ApplyTheme() => Theme.Apply(Resources, ref _palette, _vm.Theme);

        // ── running ────────────────────────────────────────────────────────────────

        private static T Data<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

        private void OnRunAction(object sender, RoutedEventArgs e) => _vm.Run(Data<PanelAction>(sender));

        private void OnSearchKey(object sender, KeyEventArgs e)
        {
            var results = _vm.SearchResults;
            switch (e.Key)
            {
                case Key.Escape:
                    if (_vm.IsSearching) { _vm.SearchText = string.Empty; e.Handled = true; }
                    break;
                case Key.Down:
                case Key.Up:
                    if (results.Count == 0) break;
                    _selected = e.Key == Key.Down ? Math.Min(results.Count - 1, _selected + 1) : Math.Max(0, _selected - 1);
                    MarkSelected();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (results.Count == 0) break;
                    _vm.Run(results[Math.Max(0, _selected)]);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>Highlights the keyboard-selected search result (Up/Down, Enter runs it).</summary>
        private void MarkSelected()
        {
            for (int i = 0; i < Results.Items.Count; i++)
            {
                if (!(Results.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter cp) || VisualTreeHelper.GetChildrenCount(cp) == 0) continue;
                if (VisualTreeHelper.GetChild(cp, 0) is Button b)
                {
                    b.Tag = i == _selected ? "selected" : null;
                    if (i == _selected) b.BringIntoView();
                }
            }
        }

        private void OnClearSearch(object sender, RoutedEventArgs e)
        {
            _vm.SearchText = string.Empty;
            Search.Focus();
        }

        // ── header ─────────────────────────────────────────────────────────────────

        private void OnReload(object sender, RoutedEventArgs e)
        {
            _vm.Reload();
            ShowToast("Plug-in list reloaded", true);
        }

        private void OnDone(object sender, RoutedEventArgs e) => _vm.IsCustomizing = false;

        private void OnOpenSep(object sender, RoutedEventArgs e) => _vm.OpenSep();

        private void OnPanelMenu(object sender, RoutedEventArgs e)
        {
            var menu = NewMenu();
            menu.Items.Add(Item("Refresh PML (rehash all)", Glyphs.Refresh, () => _vm.RunPml("PML REHASH ALL", "PML rehashed")));
            menu.Items.Add(Item("Open SEP", Glyphs.OpenApp, _vm.OpenSep));
            menu.Items.Add(Item("Open the plug-ins folder", Glyphs.Folder, () => _vm.OpenFolder(_vm.PluginsDir), enabled: !string.IsNullOrEmpty(_vm.PluginsDir)));
            menu.Items.Add(Separator());
            menu.Items.Add(Item(_vm.IsCustomizing ? "Finish customizing" : "Customize…", Glyphs.Edit, () => _vm.IsCustomizing = !_vm.IsCustomizing));
            menu.Items.Add(Item("Light theme", _vm.Theme == Theme.Light ? Glyphs.Accept : null, () => _vm.Theme = Theme.Light));
            menu.Items.Add(Item("Dark theme", _vm.Theme == Theme.Dark ? Glyphs.Accept : null, () => _vm.Theme = Theme.Dark));
            Open(menu, (FrameworkElement)sender);
        }

        // ── card and command menus ─────────────────────────────────────────────────

        private void OnCardMenuButton(object sender, RoutedEventArgs e) => OpenCardMenu(Data<PluginCard>(sender), (FrameworkElement)sender);

        private void OnCardMenu(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled) return;
            OpenCardMenu(Data<PluginCard>(sender), null);
            e.Handled = true;
        }

        private void OpenCardMenu(PluginCard card, FrameworkElement anchor)
        {
            if (card == null) return;
            var menu = NewMenu();
            foreach (var action in card.Actions)
            {
                var a = action;
                menu.Items.Add(Item(a.Caption, a.Glyph, () => _vm.Run(a), shortcut: a.Shortcut));
            }
            if (card.Actions.Count > 0) menu.Items.Add(Separator());
            if (!card.IsButtons) menu.Items.Add(Item("Show its buttons", Glyphs.Run, () => card.Mode = PanelModes.Buttons, enabled: card.HasActions));
            if (!card.IsStatusOnly) menu.Items.Add(Item("Status only (no buttons)", Glyphs.Unpin, () => card.Mode = PanelModes.Status));
            menu.Items.Add(Item("Hide from the panel", Glyphs.Cancel, () =>
            {
                card.Mode = PanelModes.Hidden;
                ShowToast(card.DisplayName + " hidden. Customize shows it again.", true);
            }));
            menu.Items.Add(Item("Customize…", Glyphs.Edit, () => _vm.IsCustomizing = true));
            menu.Items.Add(Item("Open its folder", Glyphs.Folder, () => _vm.OpenFolder(card.Path)));
            Open(menu, anchor);
        }

        private void OnActionMenu(object sender, MouseButtonEventArgs e)
        {
            var action = Data<PanelAction>(sender);
            if (action == null) return;
            e.Handled = true;
            var menu = NewMenu();
            menu.Items.Add(Item("Run", Glyphs.Run, () => _vm.Run(action), shortcut: action.Shortcut));
            menu.Items.Add(Item(action.IsPinned ? "Unpin from Quick access" : "Pin to Quick access", action.IsPinned ? Glyphs.Unpin : Glyphs.Pin, () => _vm.TogglePin(action)));
            menu.Items.Add(Item(action.HasShortcut ? "Change shortcut… (" + action.Shortcut + ")" : "Set shortcut…", Glyphs.Keyboard, () =>
            {
                _vm.RecordShortcut(action);
                ShowToast("Press the keys for " + action.Caption + " (Esc cancels)", true);
            }));
            if (action.HasShortcut) menu.Items.Add(Item("Remove shortcut", Glyphs.Cancel, () => _vm.ClearShortcut(action)));
            if (action.Plugin.IsButtons)
                menu.Items.Add(action.IsVisible
                    ? Item("Move to More", Glyphs.ChevronDown, () => _vm.SetButton(action, false))
                    : Item("Show as a button", Glyphs.ChevronUp, () => _vm.SetButton(action, true)));
            menu.Items.Add(Separator());
            menu.Items.Add(Item("Copy command", "\uE8C8", () =>
            {
                try { Clipboard.SetText(action.Command); ShowToast("Copied: " + action.Command, true); }
                catch (Exception ex) { ShowToast(ex.Message, false); }
            }));
            Open(menu, null);
        }

        private ContextMenu NewMenu()
        {
            // menus live in their own popup window: give them the palette and styles explicitly
            var menu = new ContextMenu { Style = (Style)FindResource("PanelMenu") };
            menu.Resources.MergedDictionaries.Add(_palette);
            return menu;
        }

        private MenuItem Item(string header, string glyph, Action click, bool enabled = true, string shortcut = null)
        {
            var item = new MenuItem
            {
                Header = header,
                Icon = glyph,
                InputGestureText = shortcut ?? string.Empty,
                IsEnabled = enabled,
                Style = (Style)FindResource("PanelMenuItem"),
            };
            item.Click += (s, e) =>
            {
                try { click(); }
                catch (Exception ex) { Log.Write("menu: " + ex); ShowToast(ex.Message, false); }
            };
            return item;
        }

        private Separator Separator() => new Separator { Style = (Style)FindResource("PanelMenuSeparator") };

        private static void Open(ContextMenu menu, FrameworkElement anchor)
        {
            if (anchor != null)
            {
                menu.PlacementTarget = anchor;
                menu.Placement = PlacementMode.Bottom;
            }
            else menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        // ── customize mode ─────────────────────────────────────────────────────────

        private void OnRecordShortcut(object sender, RoutedEventArgs e)
        {
            var action = Data<PanelAction>(sender);
            if (action == null) return;
            if (action.IsRecording) _vm.CancelRecording();
            else _vm.RecordShortcut(action);
        }

        private void OnClearShortcut(object sender, MouseButtonEventArgs e)
        {
            var action = Data<PanelAction>(sender);
            if (action == null) return;
            _vm.CancelRecording();
            _vm.ClearShortcut(action);
            e.Handled = true;
        }

        private void OnRecordPanelShortcut(object sender, RoutedEventArgs e)
        {
            if (_vm.IsRecordingPanel) _vm.CancelRecording();
            else _vm.RecordPanelShortcut();
        }

        private void OnClearPanelShortcut(object sender, MouseButtonEventArgs e)
        {
            _vm.CancelRecording();
            _vm.ClearPanelShortcut();
            e.Handled = true;
        }

        private void OnTogglePin(object sender, RoutedEventArgs e) => _vm.TogglePin(Data<PanelAction>(sender));
        private void OnMoveActionUp(object sender, RoutedEventArgs e) => _vm.MoveAction(Data<PanelAction>(sender), -1);
        private void OnMoveActionDown(object sender, RoutedEventArgs e) => _vm.MoveAction(Data<PanelAction>(sender), +1);
        private void OnDeleteCustom(object sender, RoutedEventArgs e) => _vm.DeleteCustom(Data<PanelAction>(sender));
        private void OnMovePluginUp(object sender, RoutedEventArgs e) => _vm.MovePlugin(Data<PluginCard>(sender), -1);
        private void OnMovePluginDown(object sender, RoutedEventArgs e) => _vm.MovePlugin(Data<PluginCard>(sender), +1);
        private void OnResetPlugin(object sender, RoutedEventArgs e) => _vm.ResetPlugin(Data<PluginCard>(sender));

        private void OnStartAdd(object sender, RoutedEventArgs e)
        {
            var card = Data<PluginCard>(sender);
            if (card != null) card.IsAdding = true;
        }

        private void OnCancelAdd(object sender, RoutedEventArgs e)
        {
            var card = Data<PluginCard>(sender);
            if (card != null) card.IsAdding = false;
        }

        private void OnConfirmAdd(object sender, RoutedEventArgs e)
        {
            var card = Data<PluginCard>(sender);
            // the two fields of this card's form: the closest panel above the button that holds them
            DependencyObject scope = sender as DependencyObject;
            TextBox caption = null, command = null;
            while (scope != null && (caption == null || command == null))
            {
                scope = VisualTreeHelper.GetParent(scope);
                caption = Find(scope, "caption");
                command = Find(scope, "command");
            }
            if (card == null || caption == null || command == null) return;
            _vm.AddCustom(card, caption.Text, command.Text);
            if (!card.IsAdding) { caption.Text = string.Empty; command.Text = string.Empty; }
        }

        private static TextBox Find(DependencyObject root, string tag)
        {
            if (root == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBox tb && Equals(tb.Tag, tag)) return tb;
                var hit = Find(child, tag);
                if (hit != null) return hit;
            }
            return null;
        }

        // ── toast ──────────────────────────────────────────────────────────────────

        private void ShowToast(string text, bool ok)
        {
            ToastText.Text = text;
            ToastIcon.Text = ok ? Glyphs.Accept : "\uE7BA";
            // the toast is dark on the light theme and light on the dark one: pick a status colour that reads on it
            bool darkToast = _vm.Theme == Theme.Light;
            ToastIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ok ? (darkToast ? "#4ADE80" : "#15803D") : (darkToast ? "#FCA5A5" : "#C62828")));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            ToastHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            ToastSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(ok ? 2.2 : 4.5);
            _toastTimer.Start();
        }

        private void HideToast()
        {
            ToastHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
            ToastSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, TimeSpan.FromMilliseconds(220)));
        }
    }

    public sealed class InverseBoolToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public sealed class CountToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public sealed class HexToBrush : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value as string ?? "#888888"));
                brush.Freeze();
                return brush;
            }
            catch { return Brushes.Gray; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>
    /// Eased wheel scrolling for the panel: each notch moves the target and the offset glides to it, so long lists
    /// scroll smoothly instead of jumping 48 px at a time.
    /// </summary>
    internal static class SmoothScroll
    {
        public static void Attach(ScrollViewer sv)
        {
            double target = 0;
            bool running = false;
            EventHandler tick = null;
            tick = (s, e) =>
            {
                double current = sv.VerticalOffset;
                double next = current + (target - current) * 0.22;
                if (Math.Abs(target - next) < 0.5) next = target;
                sv.ScrollToVerticalOffset(next);
                if (next == target)
                {
                    CompositionTarget.Rendering -= tick;
                    running = false;
                }
            };
            sv.PreviewMouseWheel += (s, e) =>
            {
                if (e.Handled || sv.ScrollableHeight <= 0) return;
                if (!running) target = sv.VerticalOffset;
                target = Math.Max(0, Math.Min(sv.ScrollableHeight, target - e.Delta * 0.6));
                if (!running)
                {
                    running = true;
                    CompositionTarget.Rendering += tick;
                }
                e.Handled = true;
            };
            sv.Unloaded += (s, e) => { if (running) { CompositionTarget.Rendering -= tick; running = false; } };
        }
    }
}
