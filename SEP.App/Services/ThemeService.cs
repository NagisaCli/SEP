using System;
using System.Linq;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SEP.App.Services;

/// <summary>
/// Dark / light / follow-Windows theme: swaps the SEP semantic brushes (Themes/SepTheme.*.xaml) and tells WPF-UI
/// to restyle its controls. Persisted as settings.theme ("auto" | "dark" | "light").
/// </summary>
public sealed class ThemeService
{
    public const string Auto = "auto";
    public const string Dark = "dark";
    public const string Light = "light";

    public string Current { get; private set; } = Auto;

    /// <summary>The theme actually on screen after resolving "auto".</summary>
    public bool IsDark { get; private set; } = true;

    public event EventHandler? Changed;

    /// <summary>
    /// Mica needs Windows 11 (build 22000+) and a composited desktop. On Windows 10, over Remote Desktop and in
    /// software-rendered VMs it leaves windows transparent or black, so SEP falls back to a solid background there.
    /// </summary>
    public static bool MicaSupported { get; } =
        Environment.OSVersion.Version.Build >= 22000
        && !SystemParameters.IsRemoteSession
        && (System.Windows.Media.RenderCapability.Tier >> 16) >= 1
        && Environment.GetEnvironmentVariable("SEP_NO_MICA") != "1";

    public static WindowBackdropType Backdrop => MicaSupported ? WindowBackdropType.Mica : WindowBackdropType.None;

    /// <summary>
    /// Every FluentWindow takes <see cref="Backdrop"/> in its XAML (x:Static), so the choice is made before the window
    /// exists: switching the backdrop of a loaded window trips WPF-UI's chrome handling.
    /// </summary>
    public static void LogBackdrop()
    {
        if (!MicaSupported)
            App.Log($"Mica unavailable (build {Environment.OSVersion.Version.Build}, remote={SystemParameters.IsRemoteSession}); using a solid window background");
    }

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Dark => Dark,
        Light => Light,
        _ => Auto,
    };

    public void Apply(string? theme)
    {
        Current = Normalize(theme);
        bool dark = Current switch
        {
            Dark => true,
            Light => false,
            _ => ApplicationThemeManager.GetSystemTheme() is not (SystemTheme.Light or SystemTheme.Flow),
        };
        ApplyResolved(dark);
    }

    private void ApplyResolved(bool dark)
    {
        IsDark = dark;
        var app = Application.Current;
        if (app == null) return;

        var dictionaries = app.Resources.MergedDictionaries;
        var sepUri = new Uri(dark ? "pack://application:,,,/Themes/SepTheme.Dark.xaml" : "pack://application:,,,/Themes/SepTheme.Light.xaml");
        var existing = dictionaries.FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("/Themes/SepTheme.", StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            dictionaries.Add(new ResourceDictionary { Source = sepUri });
        }
        else if (existing.Source == null || !existing.Source.OriginalString.Equals(sepUri.OriginalString, StringComparison.OrdinalIgnoreCase))
        {
            int index = dictionaries.IndexOf(existing);
            dictionaries[index] = new ResourceDictionary { Source = sepUri };
        }

        try
        {
            var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(theme, Backdrop, updateAccent: false);
            // WPF-UI's own accent uses (check boxes, radio buttons, focus underlines, selection pills) take systemBlue,
            // not the Windows accent. All four slots are passed: the one-colour overload would lighten the colour for the
            // dark theme (a Fluent habit), and accent text gets the theme's accessible variant.
            if (app.TryFindResource("SepTintColor") is System.Windows.Media.Color tint)
            {
                var text = app.TryFindResource("SepAccentColor") is System.Windows.Media.Color t ? t : tint;
                ApplicationAccentColorManager.Apply(tint, tint, tint, text);
            }
        }
        catch (Exception ex)
        {
            App.Log($"WPF-UI theme apply failed: {ex.Message}");
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

}
