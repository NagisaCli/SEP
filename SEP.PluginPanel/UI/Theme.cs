using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SEP.PluginPanel.UI
{
    /// <summary>
    /// The panel's two palettes, built in code so no pack URI is needed inside E3D (which has no WPF Application).
    /// The tokens carry the names and values of SEP's own theme (SEP.App/Themes/SepTheme.*.xaml: Sep{Name}Color), so the
    /// panel reads as part of SEP; a few extra ones cover what only the panel has (hover fills, toast, floating bar).
    /// When SEP's palette changes, change it here too.
    /// Each token becomes a frozen brush "{Name}" and a colour "{Name}Color".
    /// </summary>
    public static class Theme
    {
        public const string Light = "light";
        public const string Dark = "dark";

        // Static fields stay plain dictionaries: the checks load this class under mono, which has no PresentationCore.
        private static readonly Dictionary<string, string> LightColors = new Dictionary<string, string>
        {
            // surfaces (Track/Segment: a segmented control's well and its selected segment)
            ["Page"] = "#F2F4F8", ["Surface"] = "#FFFFFF", ["Surface2"] = "#F5F7FA", ["SurfaceHover"] = "#EEF2F7", ["Input"] = "#FFFFFF",
            ["Track"] = "#E9EDF3", ["Segment"] = "#FFFFFF",
            ["Border"] = "#DCE2EA", ["BorderStrong"] = "#C2CBD7", ["Divider"] = "#E8ECF2",
            // text
            ["Text"] = "#101828", ["Text2"] = "#475467", ["Text3"] = "#667085",
            // accent: SEP's teal; AccentLine is it at 40 % for hover outlines
            ["Accent"] = "#0B7BA0", ["AccentStrong"] = "#0E7490", ["AccentSoft"] = "#DFF4FA", ["AccentLine"] = "#660B7BA0", ["OnAccent"] = "#FFFFFF",
            // status
            ["Success"] = "#047857", ["SuccessSoft"] = "#DCFAEA", ["Warning"] = "#B45309", ["WarningSoft"] = "#FEF3C7",
            ["Danger"] = "#C42727", ["DangerSoft"] = "#FEE4E2", ["Neutral"] = "#667085", ["NeutralSoft"] = "#E9EDF3",
            // translucent fills: hover and press read the same on page, card and tile
            ["SubtleHover"] = "#0D0F172A", ["SubtlePressed"] = "#170F172A",
            // panel only
            ["Shadow"] = "#0F172A", ["ToastBg"] = "#1F2937", ["ToastText"] = "#F9FAFB", ["ToastBorder"] = "#00000000",
            ["ToastOk"] = "#4ADE80", ["ToastError"] = "#FCA5A5",
            ["BarBg"] = "#FAFFFFFF", ["BarBorder"] = "#1F0F172A",
            ["BrandStart"] = "#0E7FB0", ["BrandEnd"] = "#2563EB",
        };

        private static readonly Dictionary<string, string> DarkColors = new Dictionary<string, string>
        {
            ["Page"] = "#0E1117", ["Surface"] = "#161A22", ["Surface2"] = "#1C212B", ["SurfaceHover"] = "#222835", ["Input"] = "#10141B",
            ["Track"] = "#10141B", ["Segment"] = "#262D3B",
            ["Border"] = "#283040", ["BorderStrong"] = "#3A4558", ["Divider"] = "#222936",
            ["Text"] = "#F2F4F8", ["Text2"] = "#A7B0BE", ["Text3"] = "#7D8797",
            ["Accent"] = "#2DD4FF", ["AccentStrong"] = "#0EA5C9", ["AccentSoft"] = "#0F2C36", ["AccentLine"] = "#662DD4FF", ["OnAccent"] = "#06171C",
            ["Success"] = "#34D399", ["SuccessSoft"] = "#0F2E24", ["Warning"] = "#FBBF24", ["WarningSoft"] = "#33280F",
            ["Danger"] = "#F87171", ["DangerSoft"] = "#3A1719", ["Neutral"] = "#7D8797", ["NeutralSoft"] = "#262D3B",
            ["SubtleHover"] = "#0FFFFFFF", ["SubtlePressed"] = "#0AFFFFFF",
            ["Shadow"] = "#000000", ["ToastBg"] = "#262D3B", ["ToastText"] = "#F2F4F8", ["ToastBorder"] = "#3A4558",
            ["ToastOk"] = "#34D399", ["ToastError"] = "#F87171",
            ["BarBg"] = "#FA161A22", ["BarBorder"] = "#24FFFFFF",
            // a deeper start than SEP's #2DD4FF: the white glyph on the brand tile must read
            ["BrandStart"] = "#0EA5C9", ["BrandEnd"] = "#3B82F6",
        };

        /// <summary>Shadows need far more opacity on dark surfaces to be seen at all.</summary>
        private static readonly Dictionary<string, double> LightNumbers = new Dictionary<string, double> { ["ShadowOpacity"] = 0.14 };
        private static readonly Dictionary<string, double> DarkNumbers = new Dictionary<string, double> { ["ShadowOpacity"] = 0.5 };

        private static ResourceDictionary _light, _dark;

        public static string Normalize(string theme) => theme == Dark ? Dark : Light;

        /// <summary>The palette of <paramref name="theme"/>, built once: its brushes are frozen, so the views and menus share it.</summary>
        public static ResourceDictionary Build(string theme)
        {
            bool dark = Normalize(theme) == Dark;
            var built = dark ? _dark : _light;
            if (built != null) return built;
            var dict = new ResourceDictionary();
            foreach (var pair in dark ? DarkColors : LightColors)
            {
                var color = (Color)ColorConverter.ConvertFromString(pair.Value);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                dict[pair.Key + "Color"] = color;
                dict[pair.Key] = brush;
            }
            foreach (var pair in dark ? DarkNumbers : LightNumbers) dict[pair.Key] = pair.Value;
            // the app tile behind the panel's and the bar's mark: SEP's accent gradient
            var brand = new LinearGradientBrush((Color)dict["BrandStartColor"], (Color)dict["BrandEndColor"], new Point(0, 0), new Point(1, 1));
            brand.Freeze();
            dict["BrandGradient"] = brand;
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
