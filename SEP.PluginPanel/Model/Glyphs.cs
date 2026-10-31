using System;

namespace SEP.PluginPanel.Model
{
    /// <summary>
    /// Segoe MDL2 Assets glyphs (present on Windows 10 and 11) for the panel chrome and the commands. A command gets
    /// its glyph from its .uic image id and caption: the AVEVA icon resources themselves are not available outside CAF.
    /// </summary>
    public static class Glyphs
    {
        public const string Run = "\uE768";
        public const string Edit = "\uE70F";
        public const string Refresh = "\uE72C";
        public const string Pin = "\uE718";
        public const string Unpin = "\uE77A";
        public const string Keyboard = "\uE765";
        public const string Folder = "\uE8B7";
        public const string Cancel = "\uE711";
        public const string Accept = "\uE8FB";
        public const string ChevronDown = "\uE70D";
        public const string ChevronUp = "\uE70E";
        public const string OpenApp = "\uE8A7";
        public const string Theme = "\uE793";

        private static readonly (string Keyword, string Glyph)[] Map =
        {
            ("diagnos", "\uE9D9"), ("self-test", "\uE9D9"), ("selftest", "\uE9D9"),
            ("environment", "\uE946"), ("info", "\uE946"),
            ("help", "\uE897"),
            ("rehash", "\uE72C"), ("refresh", "\uE72C"), ("reload", "\uE72C"),
            ("sync", "\uE895"),
            ("paste", "\uE77F"),
            ("copy", "\uE8C8"),
            ("import", "\uE8B5"),
            ("export", "\uEDE1"),
            ("share", "\uE72D"),
            ("library", "\uE8F1"),
            ("watch", "\uE7B3"), ("monitor", "\uE7B3"),
            ("stop", "\uE71A"),
            ("clear", "\uE75C"), ("erase", "\uE75C"),
            ("delete", "\uE74D"), ("remove", "\uE74D"),
            ("hide", "\uED1A"),
            ("eye", "\uE890"), ("inspect", "\uE890"), ("hover", "\uE890"), ("view", "\uE890"),
            ("level", "\uE81E"), ("layer", "\uE81E"),
            ("search", "\uE721"), ("find", "\uE721"),
            ("setting", "\uE713"), ("config", "\uE713"), ("option", "\uE713"),
            ("revision", "\uE81C"), ("history", "\uE81C"), ("egit", "\uE81C"),
            ("drawing", "\uE7C3"), ("iso", "\uE7C3"), ("production", "\uE7C3"),
            ("report", "\uE8FD"), ("list", "\uE8FD"), ("table", "\uE8FD"),
            ("colour", "\uE790"), ("color", "\uE790"),
            ("measure", "\uECC6"), ("ruler", "\uECC6"), ("intersect", "\uECC6"), ("ray", "\uECC6"),
            ("align", "\uE8E4"),
            ("bridge", "\uE71B"), ("link", "\uE71B"), ("connect", "\uE71B"),
            ("explorer", "\uE8A7"), ("show", "\uE8A7"), ("open", "\uE8A7"),
            ("file", "\uE8A5"), ("document", "\uE8A5"),
        };

        public static string Pick(string image, string text)
        {
            // "ID_EYE", "showPipeAligner" and "Hover Inspect" all become space-separated lower-case words
            // "AvevaSharedIcons:ID_EYE": only the id says anything (the library name would match "share")
            string id = image ?? string.Empty;
            int colon = id.LastIndexOf(':');
            if (colon >= 0) id = id.Substring(colon + 1);
            if (id.StartsWith("ID_", StringComparison.OrdinalIgnoreCase)) id = id.Substring(3);
            string raw = id + " " + (text ?? string.Empty);
            string hay = System.Text.RegularExpressions.Regex.Replace(raw, "([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' ').ToLowerInvariant();
            foreach (var (keyword, glyph) in Map)
                if (StartsWord(hay, keyword)) return glyph;
            return Run;
        }

        /// <summary>The keyword begins a word ("ray" in "ray trace", not in "display").</summary>
        private static bool StartsWord(string hay, string keyword)
        {
            for (int i = hay.IndexOf(keyword, StringComparison.Ordinal); i >= 0; i = hay.IndexOf(keyword, i + 1, StringComparison.Ordinal))
                if (i == 0 || !char.IsLetter(hay[i - 1])) return true;
            return false;
        }
    }
}
