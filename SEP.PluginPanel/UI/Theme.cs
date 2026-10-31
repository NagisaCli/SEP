using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SEP.PluginPanel.UI
{
    /// <summary>
    /// The panel's two palettes, built in code so no pack URI is needed inside E3D (which has no WPF Application).
    /// Light matches E3D's own light chrome; the accent is SEP's teal.
    /// </summary>
    public static class Theme
    {
        public const string Light = "light";
        public const string Dark = "dark";

        private static readonly Dictionary<string, string> LightColors = new Dictionary<string, string>
        {
            ["PanelBg"] = "#F3F5F8", ["Surface"] = "#FFFFFF", ["Surface2"] = "#F7F9FB", ["Hover"] = "#EDF1F5", ["Pressed"] = "#E2E8EE",
            ["Border"] = "#E1E6EC", ["BorderStrong"] = "#C9D2DC", ["Text"] = "#1C2733", ["Text2"] = "#526170", ["Text3"] = "#8592A0",
            ["Accent"] = "#0891B2", ["AccentStrong"] = "#0E7490", ["AccentSoft"] = "#E2F5FA", ["OnAccent"] = "#FFFFFF",
            ["Success"] = "#15803D", ["SuccessSoft"] = "#DCF5E4", ["Warning"] = "#B45309", ["WarningSoft"] = "#FDF0D5",
            ["Danger"] = "#C62828", ["DangerSoft"] = "#FCE4E4", ["Neutral"] = "#8592A0", ["NeutralSoft"] = "#EEF1F4",
            ["Shadow"] = "#1A1C2733", ["ToastBg"] = "#1C2733", ["ToastText"] = "#FFFFFF",
            ["BarBg"] = "#F7FFFFFF", ["BarBorder"] = "#1F1C2733",
        };

        private static readonly Dictionary<string, string> DarkColors = new Dictionary<string, string>
        {
            ["PanelBg"] = "#1A1E24", ["Surface"] = "#222830", ["Surface2"] = "#272E37", ["Hover"] = "#2E3640", ["Pressed"] = "#36404B",
            ["Border"] = "#323A44", ["BorderStrong"] = "#46505C", ["Text"] = "#E7EBEF", ["Text2"] = "#A8B3BE", ["Text3"] = "#76818D",
            ["Accent"] = "#22C3E6", ["AccentStrong"] = "#5BD5EF", ["AccentSoft"] = "#163C47", ["OnAccent"] = "#0B1A1F",
            ["Success"] = "#4ADE80", ["SuccessSoft"] = "#163A26", ["Warning"] = "#FBBF24", ["WarningSoft"] = "#3D3016",
            ["Danger"] = "#F87171", ["DangerSoft"] = "#3F1F22", ["Neutral"] = "#76818D", ["NeutralSoft"] = "#2A3139",
            ["Shadow"] = "#66000000", ["ToastBg"] = "#E7EBEF", ["ToastText"] = "#1A1E24",
            ["BarBg"] = "#F5222830", ["BarBorder"] = "#3346505C",
        };

        private static ResourceDictionary _light, _dark;

        public static string Normalize(string theme) => theme == Dark ? Dark : Light;

        /// <summary>The palette of <paramref name="theme"/>, built once: its brushes are frozen, so the views and menus share it.</summary>
        public static ResourceDictionary Build(string theme)
        {
            bool dark = Normalize(theme) == Dark;
            var built = dark ? _dark : _light;
            if (built != null) return built;
            var colors = dark ? DarkColors : LightColors;
            var dict = new ResourceDictionary();
            foreach (var pair in colors)
            {
                var color = (Color)ColorConverter.ConvertFromString(pair.Value);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                dict[pair.Key + "Color"] = color;
                dict[pair.Key] = brush;
            }
            if (dark) _dark = dict;
            else _light = dict;
            return dict;
        }

        /// <summary>
        /// Puts the palette of <paramref name="theme"/> into <paramref name="resources"/> in place of
        /// <paramref name="current"/>. Nothing happens when it is there already: a swap re-resolves every
        /// DynamicResource of the view, and each reload of the panel asks again with the theme unchanged.
        /// </summary>
        public static void Apply(ResourceDictionary resources, ref ResourceDictionary current, string theme)
        {
            var palette = Build(theme);
            if (ReferenceEquals(palette, current)) return;
            if (current != null) resources.MergedDictionaries.Remove(current);
            resources.MergedDictionaries.Add(palette);
            current = palette;
        }
    }
}
