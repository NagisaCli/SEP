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
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);
            // WPF-UI's Primary buttons, toggles and selection highlights take the SEP accent, not the Windows one.
            // (Buttons already on screen keep their old fill until their page is re-created; new pages pick the new accent up.)
            if (app.TryFindResource("SepAccentStrongColor") is System.Windows.Media.Color accent)
            {
                ApplicationAccentColorManager.Apply(accent, theme);
            }
        }
        catch (Exception ex)
        {
            App.Log($"WPF-UI theme apply failed: {ex.Message}");
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

}
