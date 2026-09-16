using System;
using System.ComponentModel;
using System.Globalization;
using SEP.App.Resources;

namespace SEP.App.Localization;

/// <summary>
/// Runtime language switch for the UI. XAML binds through the indexer (see <see cref="TrExtension"/>);
/// code uses the strongly-typed <see cref="Strings"/> class directly. Changing the language raises
/// PropertyChanged for the indexer, so every bound label refreshes in place without a restart.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string Auto = "auto";
    public const string English = "en";
    public const string ChineseSimplified = "zh-CN";

    // Captured before Apply() ever touches CurrentUICulture, so "auto" keeps following Windows
    // even after the user has switched languages back and forth. Declared before Instance on
    // purpose: static initializers run in textual order and the constructor below reads it.
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;

    public static Loc Instance { get; } = new();

    private Loc() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed; view-models holding cached text re-render on this.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>The setting value in effect: "auto", "en" or "zh-CN".</summary>
    public string Language { get; private set; } = Auto;

    /// <summary>The culture actually used for lookups after resolving "auto".</summary>
    public CultureInfo Culture { get; private set; } = Resolve(Auto);

    /// <summary>Indexer used by XAML bindings: <c>{Binding [Nav_Projects], Source={x:Static loc:Loc.Instance}}</c>.</summary>
    public string this[string key] => Strings.ResourceManager.GetString(key, Culture) ?? $"!{key}!";

    /// <summary>Applies a language setting ("auto", "en", "zh-CN"). Unknown values fall back to "auto".</summary>
    public void Apply(string? language)
    {
        string normalized = Normalize(language);
        var culture = Resolve(normalized);
        bool changed = !culture.Equals(Culture) || normalized != Language;

        Language = normalized;
        Culture = culture;
        Strings.Culture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;

        if (!changed) return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string Normalize(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "en" or "en-us" or "en-gb" or "english" => English,
        "zh" or "zh-cn" or "zh-hans" or "zh-hans-cn" or "chinese" => ChineseSimplified,
        _ => Auto,
    };

    private static CultureInfo Resolve(string language)
    {
        if (language == English) return CultureInfo.GetCultureInfo("en");
        if (language == ChineseSimplified) return CultureInfo.GetCultureInfo("zh-CN");

        // "auto": Chinese only when Windows itself runs in Chinese; everything else gets English.
        string os = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
        string user = SystemUiCulture.TwoLetterISOLanguageName;
        return user == "zh" || os == "zh" ? CultureInfo.GetCultureInfo("zh-CN") : CultureInfo.GetCultureInfo("en");
    }
}
