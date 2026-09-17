using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SEP.App.Diagnostics;

/// <summary>
/// UI-check aid: <c>SEP.exe --shot-dir=&lt;folder&gt;</c> makes the app watch that folder; dropping <c>name.req</c>
/// there renders the window called <c>name</c> ("main" = the shell, otherwise a window title or class name)
/// to <c>name.png</c> straight from the visual tree, so the window can stay parked off-screen and never has
/// to be shown to take a picture. Inactive unless the switch is given.
/// </summary>
internal static class ScreenshotHook
{
    private static FileSystemWatcher? _watcher;

    public static void Start(string dir)
    {
        Directory.CreateDirectory(dir);
        _watcher = new FileSystemWatcher(dir, "*.req") { EnableRaisingEvents = true };
        _watcher.Created += (_, e) => Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => Capture(e.FullPath));
        App.Log($"screenshot hook watching {dir}");
    }

    private static void Capture(string reqPath)
    {
        string name = Path.GetFileNameWithoutExtension(reqPath);
        string target = Path.ChangeExtension(reqPath, ".png");
        try
        {
            var window = name.Equals("main", StringComparison.OrdinalIgnoreCase)
                ? Application.Current.MainWindow
                : Application.Current.Windows.OfType<Window>().FirstOrDefault(w =>
                    string.Equals(w.Title, name, StringComparison.OrdinalIgnoreCase) || string.Equals(w.GetType().Name, name, StringComparison.OrdinalIgnoreCase));
            if (window == null || window.ActualWidth < 1 || window.ActualHeight < 1)
            {
                File.WriteAllText(Path.ChangeExtension(reqPath, ".err"), $"window '{name}' not found or not laid out");
                return;
            }

            int w = (int)Math.Ceiling(window.ActualWidth);
            int h = (int)Math.Ceiling(window.ActualHeight);
            // Mica leaves the window background transparent; paint the page colour underneath so the picture reads like the screen.
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var page = Application.Current.TryFindResource("SepPageBrush") as Brush ?? Brushes.Black;
                dc.DrawRectangle(page, null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(window) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
            }
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(target);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.ChangeExtension(reqPath, ".err"), ex.ToString());
        }
        finally
        {
            try { File.Delete(reqPath); } catch { }
        }
    }
}
