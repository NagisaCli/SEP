using System;
using System.Collections.Generic;
using System.Linq;
using Keys = System.Windows.Forms.Keys;

namespace SEP.PluginPanel.Model
{
    /// <summary>
    /// A keyboard shortcut such as "Ctrl+Shift+K" or "F9": a key plus modifiers, stored as text in the user layout.
    /// Only chords that cannot be ordinary typing are accepted: Ctrl or Alt with any key, or a function key. Chords
    /// E3D or Windows already use are refused on top of that (<see cref="ReservedFor"/>, IPanelHost.E3dShortcuts).
    /// </summary>
    public static class Shortcut
    {
        public const Keys ModifierMask = Keys.Control | Keys.Alt | Keys.Shift;

        public static bool IsModifierKey(Keys key)
        {
            key &= Keys.KeyCode;
            return key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
                or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin or Keys.None;
        }

        public static bool IsFunctionKey(Keys key)
        {
            key &= Keys.KeyCode;
            return key >= Keys.F1 && key <= Keys.F24;
        }

        /// <summary>Whether <paramref name="chord"/> may be used as a shortcut (see the class remarks).</summary>
        public static bool IsAssignable(Keys chord)
        {
            Keys key = chord & Keys.KeyCode;
            if (IsModifierKey(key)) return false;
            if (IsFunctionKey(key)) return true;
            if ((chord & (Keys.Control | Keys.Alt)) == 0) return false;
            // Alt+F4 / Alt+Tab / Ctrl+Esc belong to Windows
            if ((chord & Keys.Alt) != 0 && key is Keys.F4 or Keys.Tab) return false;
            if (key == Keys.Escape) return false;
            return true;
        }

        /// <summary>
        /// What Windows does with <paramref name="chord"/> in E3D's command window, forms and windows ("copy"), or
        /// null. A shortcut on one of these would stop it working everywhere in E3D.
        /// </summary>
        public static string ReservedFor(Keys chord)
        {
            Keys key = chord & Keys.KeyCode;
            Keys mods = chord & ModifierMask;
            if (mods == Keys.Control)
            {
                switch (key)
                {
                    case Keys.C: case Keys.Insert: return "copy";
                    case Keys.V: return "paste";
                    case Keys.X: return "cut";
                    case Keys.A: return "select all";
                    case Keys.Z: return "undo";
                    case Keys.Y: return "redo";
                    case Keys.Back: case Keys.Delete: return "delete a word";
                    case Keys.F4: return "close the window";
                    case Keys.F6: return "next window";
                }
            }
            if (mods == Keys.Control || mods == (Keys.Control | Keys.Shift))
            {
                switch (key)
                {
                    case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End: return "moving through text";
                    case Keys.Tab: case Keys.PageUp: case Keys.PageDown: return "switching tabs";
                }
            }
            if (mods == Keys.Alt && key == Keys.Space) return "the window menu";
            if (mods == Keys.Alt && key is Keys.Up or Keys.Down) return "opening a drop-down list";
            if (mods == Keys.Shift && key == Keys.F10) return "the context menu";
            return null;
        }

        public static string Format(Keys chord)
        {
            if (chord == Keys.None) return null;
            var parts = new List<string>();
            if ((chord & Keys.Control) != 0) parts.Add("Ctrl");
            if ((chord & Keys.Alt) != 0) parts.Add("Alt");
            if ((chord & Keys.Shift) != 0) parts.Add("Shift");
            parts.Add(KeyName(chord & Keys.KeyCode));
            return string.Join("+", parts);
        }

        public static Keys Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Keys.None;
            Keys result = Keys.None;
            foreach (string raw in text.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "ctrl": case "control": result |= Keys.Control; continue;
                    case "alt": result |= Keys.Alt; continue;
                    case "shift": result |= Keys.Shift; continue;
                }
                Keys key = KeyFromName(raw);
                if (key == Keys.None) return Keys.None;
                result |= key;
            }
            return (result & Keys.KeyCode) == Keys.None ? Keys.None : result;
        }

        /// <summary>Normalised text of a stored shortcut ("ctrl+k" → "Ctrl+K"), null when it is not a valid shortcut.</summary>
        public static string Normalize(string text)
        {
            Keys chord = Parse(text);
            return chord != Keys.None && IsAssignable(chord) ? Format(chord) : null;
        }

        private static readonly Dictionary<Keys, string> Names = new Dictionary<Keys, string>
        {
            [Keys.D0] = "0", [Keys.D1] = "1", [Keys.D2] = "2", [Keys.D3] = "3", [Keys.D4] = "4",
            [Keys.D5] = "5", [Keys.D6] = "6", [Keys.D7] = "7", [Keys.D8] = "8", [Keys.D9] = "9",
            [Keys.Oemcomma] = ",", [Keys.OemPeriod] = ".", [Keys.OemMinus] = "-", [Keys.Oemplus] = "=",
            [Keys.OemQuestion] = "/", [Keys.OemSemicolon] = ";", [Keys.OemQuotes] = "'", [Keys.OemOpenBrackets] = "[",
            [Keys.OemCloseBrackets] = "]", [Keys.OemPipe] = "\\", [Keys.Oemtilde] = "`",
            [Keys.Return] = "Enter", [Keys.Back] = "Backspace", [Keys.Next] = "PageDown", [Keys.Prior] = "PageUp",
            [Keys.Space] = "Space", [Keys.Delete] = "Del", [Keys.Insert] = "Ins",
        };

        private static string KeyName(Keys key)
        {
            if (Names.TryGetValue(key, out string name)) return name;
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return "Num" + (key - Keys.NumPad0);
            return key.ToString();
        }

        private static Keys KeyFromName(string name)
        {
            foreach (var pair in Names)
                if (string.Equals(pair.Value, name, StringComparison.OrdinalIgnoreCase)) return pair.Key;
            if (name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && name.Length == 4 && char.IsDigit(name[3]))
                return Keys.NumPad0 + (name[3] - '0');
            if (name.Length == 1 && char.IsDigit(name[0])) return Keys.D0 + (name[0] - '0');
            return Enum.TryParse(name, true, out Keys key) && (key & ~Keys.KeyCode) == 0 ? key : Keys.None;
        }
    }
}
