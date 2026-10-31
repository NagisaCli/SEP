using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using SEP.PluginPanel.Model;

namespace SEP.PluginPanel.UI
{
    /// <summary>
    /// The floating bar's content. The window around it (BarWindow, or the preview's) moves it, keeps it with E3D
    /// and decides when it may take the keyboard (only while searching).
    /// </summary>
    public partial class BarView : UserControl
    {
        private enum FlyoutKind { None, Plugin, Menu, Search }

        private readonly PanelViewModel _vm;
        private ResourceDictionary _palette;
        private readonly DispatcherTimer _toastTimer = new DispatcherTimer();
        private FlyoutKind _flyout;
        private Button _flyoutAnchor;
        private List<Button> _resultRows = new List<Button>();
        private int _selected = -1;
        private bool _searching;

        public BarView(PanelViewModel vm)
        {
            _vm = vm;
            ApplyTheme();
            InitializeComponent();
            DataContext = vm;
            vm.ThemeChanged += ApplyTheme;
            vm.Toast += ShowToast;
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(PanelViewModel.IsBarVertical)) CloseFlyout();
            };
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); HideToast(); };
            Shell.MouseEnter += (s, e) => Fade(CloseButton, 0.85, 160);
            Shell.MouseLeave += (s, e) => { if (!_searching) Fade(CloseButton, 0, 260); };
        }

        public PanelViewModel ViewModel => _vm;

        /// <summary>The user dragged the bar to a new place (the window saves it).</summary>
        public event Action Moved;

        /// <summary>Hide the bar (its × or the menu).</summary>
        public event Action CloseRequested;

        /// <summary>Search opened (true: the window must take the keyboard) or closed (false: give it back to E3D).</summary>
        public event Action<bool> SearchModeChanged;

        public bool IsSearching => _searching;

        private void ApplyTheme() => Theme.Apply(Resources, ref _palette, _vm.Theme);

        // ── entrance and exit ──────────────────────────────────────────────────────

        /// <summary>A short drop-in: fade, a few pixels of slide and a slight grow.</summary>
        public void PlayEnter()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var t = Transforms(Shell);
            Shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { FillBehavior = FillBehavior.Stop });
            t.Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_vm.IsBarVertical ? 0 : -8, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            t.Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_vm.IsBarVertical ? -8 : 0, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }

        /// <summary>Fades out, then calls <paramref name="done"/> (the window hides itself there).</summary>
        public void PlayExit(Action done)
        {
            CloseFlyout();
            HideToast();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop };
            fade.Completed += (s, e) => done();
            var t = Transforms(Shell);
            t.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.98, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop });
            t.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.98, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop });
            Shell.BeginAnimation(OpacityProperty, fade);
        }

        private static (ScaleTransform Scale, TranslateTransform Slide) Transforms(FrameworkElement fe)
        {
            if (fe.RenderTransform is TransformGroup g && g.Children.Count == 2 && g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t && !g.IsFrozen)
                return (s, t);
            var scale = new ScaleTransform();
            var slide = new TranslateTransform();
            fe.RenderTransformOrigin = new Point(0.5, 0.5);
            fe.RenderTransform = new TransformGroup { Children = { scale, slide } };
            return (scale, slide);
        }

        private static void Fade(UIElement element, double to, int ms) =>
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        // ── dragging and closing ───────────────────────────────────────────────────

        private void OnGrip(object sender, MouseButtonEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window == null || e.ButtonState != MouseButtonState.Pressed) return;
            CloseFlyout();
            try { window.DragMove(); } catch (InvalidOperationException) { return; }
            Moved?.Invoke();
        }

        private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

        // ── commands ───────────────────────────────────────────────────────────────

        private static T Data<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

        private void OnRunAction(object sender, RoutedEventArgs e) => Run(Data<PanelAction>(sender));

        private void Run(PanelAction action)
        {
            if (action == null) return;
            CloseFlyout();
            if (_searching) CloseSearch();
            _vm.Run(action);
        }

        private void OnActionMenu(object sender, MouseButtonEventArgs e)
        {
            var action = Data<PanelAction>(sender);
            if (action == null) return;
            e.Handled = true;
            TogglePin(action);
        }

        private void TogglePin(PanelAction action)
        {
            _vm.TogglePin(action);
            ShowToast(action.IsPinned ? action.Caption + " pinned to the bar" : action.Caption + " unpinned", true);
        }

        private void OnPluginTile(object sender, RoutedEventArgs e)
        {
            var card = Data<PluginCard>(sender);
            var anchor = (Button)sender;
            if (card == null) return;
            if (_flyout == FlyoutKind.Plugin && _flyoutAnchor == anchor) { CloseFlyout(); return; }
            // one command: the tile is the button
            var actions = FlyoutActions(card);
            if (actions.Count == 1 && card.State != PluginState.Missing) { Run(actions[0]); return; }
            OpenPluginFlyout(card, anchor);
        }

        private void OnPluginMenu(object sender, MouseButtonEventArgs e)
        {
            var card = Data<PluginCard>(sender);
            if (card == null) return;
            e.Handled = true;
            OpenPluginFlyout(card, (Button)sender);
        }

        private static List<PanelAction> FlyoutActions(PluginCard card) => card.ButtonActions.Concat(card.MoreActions).ToList();

        // ── flyout: a plug-in's commands ───────────────────────────────────────────

        private void OpenPluginFlyout(PluginCard card, Button anchor)
        {
            var content = BeginFlyout(FlyoutKind.Plugin, anchor);
            content.Children.Add(PluginHeader(card));
            var actions = FlyoutActions(card);
            if (actions.Count == 0) content.Children.Add(Hint("No commands. Customize the plug-in in the panel."));
            int buttons = card.ButtonActions.Count;
            for (int i = 0; i < actions.Count; i++)
            {
                if (i == buttons && buttons > 0) content.Children.Add(Separator());
                content.Children.Add(ActionRow(actions[i], subtitle: null));
            }
            content.Children.Add(Hint("Right-click a command to pin it to the bar"));
            ShowFlyout();
        }

        private FrameworkElement PluginHeader(PluginCard card)
        {
            var grid = new Grid { Margin = new Thickness(6, 4, 6, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var avatar = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(8),
                Background = (Brush)new HexToSoftBrush().Convert(card.AvatarColor, typeof(Brush), null, CultureInfo.InvariantCulture),
                Child = new TextBlock
                {
                    Text = card.Initials, FontWeight = FontWeights.Bold, FontSize = 11,
                    Foreground = AvatarInk.Ink(card.AvatarColor, _vm.Theme == Theme.Dark),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            grid.Children.Add(avatar);
            var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = card.DisplayName, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            var state = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            string tone = card.State == PluginState.Ready ? "Success" : card.State == PluginState.NotLoaded ? "Warning" : card.State == PluginState.Missing ? "Danger" : "Accent";
            state.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = (Brush)FindResource(tone), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            state.Children.Add(new TextBlock { Text = card.StateText, FontSize = 11, Foreground = (Brush)FindResource("Text2") });
            text.Children.Add(state);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            grid.ToolTip = string.IsNullOrWhiteSpace(card.StateDetail) ? null : card.StateDetail;
            if (card.HasDescription)
            {
                var wrap = new StackPanel();
                wrap.Children.Add(grid);
                wrap.Children.Add(new TextBlock
                {
                    Text = card.Description, FontSize = 11, Foreground = (Brush)FindResource("Text2"), TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8, 0, 8, 6), MaxHeight = 46, TextTrimming = TextTrimming.WordEllipsis,
                });
                return wrap;
            }
            return grid;
        }

        private Button ActionRow(PanelAction action, string subtitle)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = (Brush)FindResource("AccentSoft"),
                Child = new TextBlock { Text = action.Glyph, Style = (Style)FindResource("Glyph"), FontSize = 14, Foreground = (Brush)FindResource("Accent") },
            });
            var text = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = action.Caption, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(subtitle))
                text.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = (Brush)FindResource("Text3"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (action.HasShortcut)
                right.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Background = (Brush)FindResource("NeutralSoft"),
                    BorderBrush = (Brush)FindResource("Border"), BorderThickness = new Thickness(1, 1, 1, 2),
                    Child = new TextBlock { Text = action.Shortcut, FontSize = 10, Foreground = (Brush)FindResource("Text2") },
                });
            if (action.IsPinned)
                right.Children.Add(new TextBlock { Text = Glyphs.Pin, Style = (Style)FindResource("Glyph"), FontSize = 10, Foreground = (Brush)FindResource("Accent"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Pinned to the bar" });
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            var row = new Button { Style = (Style)FindResource("RowButton"), Content = grid, DataContext = action, ToolTip = action.ToolTipText };
            ToolTipService.SetInitialShowDelay(row, 900);
            ToolTipService.SetPlacement(row, PlacementMode.Right);
            row.Click += (s, e) => Run(action);
            row.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                TogglePin(action);
                if (_flyout == FlyoutKind.Plugin) OpenPluginFlyout(action.Plugin, _flyoutAnchor);
                else if (_flyout == FlyoutKind.Search) FillSearchResults();
            };
            return row;
        }

        // ── flyout: the SEP menu ───────────────────────────────────────────────────

        private void OnBrandMenu(object sender, RoutedEventArgs e)
        {
            if (_flyout == FlyoutKind.Menu) { CloseFlyout(); return; }
            var content = BeginFlyout(FlyoutKind.Menu, BrandButton);
            var head = new StackPanel { Margin = new Thickness(8, 4, 8, 6) };
            head.Children.Add(new TextBlock { Text = "SEP plug-ins", FontWeight = FontWeights.SemiBold, FontSize = 13 });
            head.Children.Add(new TextBlock { Text = _vm.Summary, FontSize = 11, Foreground = (Brush)FindResource(_vm.AttentionCount > 0 ? "Warning" : "Text2"), Margin = new Thickness(0, 2, 0, 0) });
            content.Children.Add(head);
            content.Children.Add(MenuRow("Manage plug-ins…", Glyphs.Edit, "Buttons, pins, shortcuts and status in the side panel", () => _vm.ShowPanel()));
            content.Children.Add(MenuRow("Refresh PML", Glyphs.Refresh, "PML REHASH ALL", () => _vm.RunPml("PML REHASH ALL", "PML rehashed")));
            content.Children.Add(MenuRow("Open SEP", Glyphs.OpenApp, null, _vm.OpenSep));
            content.Children.Add(Separator());
            content.Children.Add(MenuRow(_vm.IsBarVertical ? "Lay the bar flat" : "Stand the bar up", _vm.IsBarVertical ? "" : "", null,
                () => _vm.IsBarVertical = !_vm.IsBarVertical));
            content.Children.Add(MenuRow(_vm.Theme == Theme.Dark ? "Light look" : "Dark look", Glyphs.Theme, null,
                () => _vm.Theme = _vm.Theme == Theme.Dark ? Theme.Light : Theme.Dark));
            content.Children.Add(MenuRow("Hide the bar", Glyphs.Cancel, "The ribbon's Plug-ins button brings it back", () => CloseRequested?.Invoke()));
            ShowFlyout();
        }

        private Button MenuRow(string caption, string glyph, string subtitle, Action action)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock { Text = glyph ?? string.Empty, Style = (Style)FindResource("Glyph"), FontSize = 13, Foreground = (Brush)FindResource("Text2") });
            var text = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = caption });
            if (!string.IsNullOrEmpty(subtitle))
                text.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = (Brush)FindResource("Text3"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            var row = new Button { Style = (Style)FindResource("RowButton"), Content = grid, MinHeight = 32 };
            row.Click += (s, e) => { CloseFlyout(); action(); };
            return row;
        }

        // ── search ─────────────────────────────────────────────────────────────────

        private void OnSearchButton(object sender, RoutedEventArgs e)
        {
            if (_searching) CloseSearch();
            else OpenSearch();
        }

        /// <summary>Opens the search field (the ribbon's "Find command", the panel shortcut).</summary>
        public void OpenSearch()
        {
            CloseFlyout();
            if (!_searching)
            {
                _searching = true;
                SearchModeChanged?.Invoke(true);
                SearchField.Visibility = Visibility.Visible;
                SearchField.BeginAnimation(WidthProperty, new DoubleAnimation(0, 230, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                SearchButton.Tag = "open";
                Fade(CloseButton, 0.85, 160);
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                Search.Focus();
                Keyboard.Focus(Search);
                Search.SelectAll();
            }));
            UpdateHint();
        }

        public void CloseSearch()
        {
            if (!_searching) return;
            _searching = false;
            if (_flyout == FlyoutKind.Search) CloseFlyout();
            _vm.SearchText = string.Empty;
            SearchButton.Tag = null;
            var shrink = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            shrink.Completed += (s, e) => { if (!_searching) SearchField.Visibility = Visibility.Collapsed; };
            SearchField.BeginAnimation(WidthProperty, shrink);
            if (!Shell.IsMouseOver) Fade(CloseButton, 0, 260);
            SearchModeChanged?.Invoke(false);
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            UpdateHint();
            if (!_searching) return;
            if (_vm.IsSearching) FillSearchResults();
            else if (_flyout == FlyoutKind.Search) CloseFlyout();
        }

        private void UpdateHint() => SearchHint.Visibility = string.IsNullOrEmpty(Search.Text) ? Visibility.Visible : Visibility.Collapsed;

        private void FillSearchResults()
        {
            var results = _vm.SearchResults;
            var content = BeginFlyout(FlyoutKind.Search, null, keepOpen: _flyout == FlyoutKind.Search);
            _resultRows = new List<Button>();
            if (results.Count == 0) content.Children.Add(Hint("No command matches “" + _vm.SearchText.Trim() + "”"));
            foreach (var action in results.Take(12))
            {
                var row = ActionRow(action, action.Plugin.DisplayName);
                _resultRows.Add(row);
                content.Children.Add(row);
            }
            if (results.Count > 12) content.Children.Add(Hint((results.Count - 12) + " more: type more of the name"));
            _selected = _resultRows.Count > 0 ? 0 : -1;
            MarkSelected();
            ShowFlyout();
        }

        private void OnSearchKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    if (_vm.IsSearching) _vm.SearchText = string.Empty;
                    else CloseSearch();
                    e.Handled = true;
                    break;
                case Key.Down:
                case Key.Up:
                    if (_resultRows.Count == 0) break;
                    _selected = e.Key == Key.Down ? Math.Min(_resultRows.Count - 1, _selected + 1) : Math.Max(0, _selected - 1);
                    MarkSelected();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (_flyout == FlyoutKind.Search && _selected >= 0 && _selected < _resultRows.Count) Run(_resultRows[_selected].DataContext as PanelAction);
                    e.Handled = true;
                    break;
            }
        }

        private void MarkSelected()
        {
            for (int i = 0; i < _resultRows.Count; i++) _resultRows[i].Tag = i == _selected ? "selected" : null;
        }

        // ── the flyout itself ──────────────────────────────────────────────────────

        private StackPanel BeginFlyout(FlyoutKind kind, Button anchor, bool keepOpen = false)
        {
            if (!keepOpen) CloseFlyout();
            _flyout = kind;
            _flyoutAnchor = anchor;
            if (anchor != null) anchor.Tag = "open";
            FlyoutContent.Children.Clear();
            FlyoutRoot.MaxHeight = 460;
            FlyoutRoot.MinWidth = kind == FlyoutKind.Search ? 300 : 230;
            bool vertical = _vm.IsBarVertical;
            Flyout.PlacementTarget = kind == FlyoutKind.Search ? (UIElement)SearchField : anchor;
            Flyout.Placement = vertical ? PlacementMode.Right : PlacementMode.Bottom;
            Flyout.HorizontalOffset = vertical ? -6 : -14;
            Flyout.VerticalOffset = vertical ? -14 : -6;
            // search results stay while you type; menus close on a click elsewhere
            Flyout.StaysOpen = kind == FlyoutKind.Search;
            return FlyoutContent;
        }

        private void ShowFlyout()
        {
            if (Flyout.IsOpen) return;
            Flyout.IsOpen = true;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var t = Transforms(FlyoutRoot);
            bool vertical = _vm.IsBarVertical;
            FlyoutRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
            t.Slide.BeginAnimation(vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty, new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }

        private void CloseFlyout()
        {
            if (_flyoutAnchor != null && _flyoutAnchor != SearchButton) _flyoutAnchor.Tag = null;
            _flyoutAnchor = null;
            _flyout = FlyoutKind.None;
            _resultRows = new List<Button>();
            if (Flyout.IsOpen) Flyout.IsOpen = false;
        }

        private void OnFlyoutClosed(object sender, EventArgs e)
        {
            // closed by a click elsewhere
            if (_flyoutAnchor != null && _flyoutAnchor != SearchButton) _flyoutAnchor.Tag = null;
            _flyoutAnchor = null;
            _flyout = FlyoutKind.None;
        }

        private FrameworkElement Separator() => new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4), Background = (Brush)FindResource("Divider") };

        private FrameworkElement Hint(string text) => new TextBlock
        {
            Text = text, FontSize = 11, Foreground = (Brush)FindResource("Text3"), Margin = new Thickness(8, 6, 8, 2), TextWrapping = TextWrapping.Wrap,
        };

        // ── toast ──────────────────────────────────────────────────────────────────

        public void ShowToast(string text, bool ok)
        {
            ToastText.Text = text;
            ToastIcon.Text = ok ? "" : "";
            // the palette's toast tones read on its own toast background (dark on both looks)
            ToastIcon.Foreground = (Brush)FindResource(ok ? "ToastOk" : "ToastError");
            ToastPopup.Placement = _vm.IsBarVertical ? PlacementMode.Right : PlacementMode.Bottom;
            if (!IsVisible) return;
            ToastPopup.IsOpen = true;
            ToastBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromMilliseconds(ok ? 1800 : 5000);
            _toastTimer.Start();
        }

        private void HideToast()
        {
            if (!ToastPopup.IsOpen) return;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (s, e) => { if (ToastBorder.Opacity < 0.05) ToastPopup.IsOpen = false; };
            ToastBorder.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>A plug-in's colour, faint: the tile behind its initials.</summary>
    public sealed class HexToSoftBrush : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(value as string ?? "#888888");
                var brush = new SolidColorBrush(Color.FromArgb(0x24, c.R, c.G, c.B));
                brush.Freeze();
                return brush;
            }
            catch { return Brushes.Transparent; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public static class BarConverters
    {
        public static readonly IValueConverter Orientation = new OrientationConverter();

        private sealed class OrientationConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
                value is bool vertical && vertical ? System.Windows.Controls.Orientation.Vertical : System.Windows.Controls.Orientation.Horizontal;

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
        }
    }
}
