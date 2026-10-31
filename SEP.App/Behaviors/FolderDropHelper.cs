using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.Localization;
using SEP.App.Resources;
using SEP.App.Services;
using SEP.App.ViewModels;

namespace SEP.App.Behaviors;

/// <summary>
/// Attached behavior enabling external folder / archive drag-and-drop from Windows Explorer.
/// Supports adding project libraries in ProjectsPage or importing plugins in PluginsPage.
/// </summary>
public static class FolderDropHelper
{
    public static readonly DependencyProperty DropModeProperty = DependencyProperty.RegisterAttached(
        "DropMode", typeof(string), typeof(FolderDropHelper), new PropertyMetadata(null, OnDropModeChanged));

    public static string? GetDropMode(DependencyObject d) => (string?)d.GetValue(DropModeProperty);
    public static void SetDropMode(DependencyObject d, string? value) => d.SetValue(DropModeProperty, value);

    private static readonly DependencyProperty OverlayAdornerProperty = DependencyProperty.RegisterAttached(
        "OverlayAdorner", typeof(DropOverlayAdorner), typeof(FolderDropHelper), new PropertyMetadata(null));

    private static void OnDropModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        string? mode = e.NewValue as string;

        if (!string.IsNullOrEmpty(mode))
        {
            fe.AllowDrop = true;
            fe.DragEnter += OnDragEnter;
            fe.DragOver += OnDragOver;
            fe.DragLeave += OnDragLeave;
            fe.Drop += OnDrop;
        }
        else
        {
            fe.DragEnter -= OnDragEnter;
            fe.DragOver -= OnDragOver;
            fe.DragLeave -= OnDragLeave;
            fe.Drop -= OnDrop;
        }
    }

    private static void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;

        if (sender is FrameworkElement fe)
        {
            ShowOverlay(fe);
        }
        e.Handled = true;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private static void OnDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            HideOverlay(fe);
        }
    }

    private static async void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        HideOverlay(fe);

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null || files.Length == 0) return;
        // must be set before the first await: once this handler yields, the drop has finished routing
        e.Handled = true;

        ToastService? toasts = null;
        // async void: an exception escaping here would reach the dispatcher and take the app down
        try
        {
            toasts = App.Services.GetService<ToastService>();
            await ImportDroppedAsync(GetDropMode(fe)?.ToLowerInvariant(), files, toasts);
        }
        catch (Exception ex)
        {
            App.Log($"folder drop failed: {ex}");
            toasts?.Error(ex.Message);
        }
    }

    private static async Task ImportDroppedAsync(string? mode, string[] files, ToastService? toasts)
    {
        if (mode == "library")
        {
            var catalog = App.Services.GetService<IProjectCatalog>();
            if (catalog == null) return;

            foreach (var path in files)
            {
                string targetDir = path;
                if (File.Exists(path))
                {
                    string dir = Path.GetDirectoryName(path) ?? path;
                    string name = Path.GetFileName(path);
                    if (name.StartsWith("evars", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                    {
                        // Check if parent directory is a project library
                        string? parent = Directory.GetParent(dir)?.FullName;
                        targetDir = !string.IsNullOrEmpty(parent) ? parent : dir;
                    }
                    else
                    {
                        targetDir = dir;
                    }
                }

                if (Directory.Exists(targetDir))
                {
                    var (ok, msg) = await catalog.AddLibraryAsync(targetDir);
                    if (ok)
                    {
                        toasts?.Success(string.Format(Strings.Lib_Added, Path.GetFileName(targetDir)));
                    }
                    else
                    {
                        toasts?.Warning(msg);
                    }
                }
            }
        }
        else if (mode == "plugin")
        {
            var plugins = App.Services.GetService<PluginService>();
            var vm = App.Services.GetService<PluginsViewModel>();

            foreach (var path in files)
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    if (plugins != null)
                    {
                        var (ok, msg, _) = await plugins.ImportAsync(path);
                        if (ok) toasts?.Success(msg);
                        else toasts?.Warning(msg);
                    }
                }
            }
        }
    }

    private static void ShowOverlay(FrameworkElement fe)
    {
        var layer = AdornerLayer.GetAdornerLayer(fe);
        if (layer == null) return;

        if (fe.GetValue(OverlayAdornerProperty) is DropOverlayAdorner existing)
        {
            layer.Remove(existing);
        }

        string mode = GetDropMode(fe)?.ToLowerInvariant() ?? "";
        string message = mode == "plugin" ? Strings.Drop_PluginArchive : Strings.Drop_ProjectLibrary;

        var adorner = new DropOverlayAdorner(fe, message);
        layer.Add(adorner);
        fe.SetValue(OverlayAdornerProperty, adorner);
    }

    private static void HideOverlay(FrameworkElement fe)
    {
        if (fe.GetValue(OverlayAdornerProperty) is DropOverlayAdorner adorner)
        {
            var layer = AdornerLayer.GetAdornerLayer(fe);
            layer?.Remove(adorner);
            fe.SetValue(OverlayAdornerProperty, null);
        }
    }

    private sealed class DropOverlayAdorner : Adorner
    {
        private readonly string _message;
        // theme colours, read once per drag: a veil of the page colour with the accent's dashed outline
        private readonly Brush _overlayBg;
        private readonly Pen _borderPen;
        private readonly Brush _textBrush;

        public DropOverlayAdorner(UIElement adornedElement, string message) : base(adornedElement)
        {
            _message = message;
            IsHitTestVisible = false;
            var app = Application.Current;
            var page = app?.TryFindResource("SepPageColor") is Color p ? p : Color.FromRgb(0x0E, 0x11, 0x17);
            var accent = app?.TryFindResource("SepAccentColor") is Color a ? a : Color.FromRgb(0x2D, 0xD4, 0xFF);
            _overlayBg = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, page.R, page.G, page.B)));
            _textBrush = Freeze(new SolidColorBrush(accent));
            _borderPen = Freeze(new Pen(_textBrush, 2) { DashStyle = new DashStyle(new double[] { 5, 3 }, 0) });
        }

        protected override void OnRender(DrawingContext dc)
        {
            var rect = new Rect(new Point(6, 6), new Size(Math.Max(0, AdornedElement.RenderSize.Width - 12), Math.Max(0, AdornedElement.RenderSize.Height - 12)));
            dc.DrawRoundedRectangle(_overlayBg, _borderPen, rect, 12, 12);

            var ft = new FormattedText(
                _message,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                16,
                _textBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            double x = rect.X + (rect.Width - ft.Width) / 2.0;
            double y = rect.Y + (rect.Height - ft.Height) / 2.0;
            dc.DrawText(ft, new Point(Math.Max(rect.X + 10, x), Math.Max(rect.Y + 10, y)));
        }

        private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
    }
}
