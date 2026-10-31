using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SEP.PluginPanel.Model
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public enum PluginState
    {
        /// <summary>Everything the plug-in needs is in this session (add-in loaded, PML on PMLLIB).</summary>
        Ready,
        /// <summary>A required add-in, command or PML folder is unavailable in this session.</summary>
        NotLoaded,
        /// <summary>Its folder is gone.</summary>
        Missing,
        /// <summary>Nothing to check (no add-in, no PML folder).</summary>
        Enabled,
    }

    public static class PanelModes
    {
        public const string Buttons = "buttons";
        public const string Status = "status";
        public const string Hidden = "hidden";

        public static string Normalize(string mode) =>
            string.Equals(mode, Status, StringComparison.OrdinalIgnoreCase) ? Status
            : string.Equals(mode, Hidden, StringComparison.OrdinalIgnoreCase) ? Hidden
            : Buttons;
    }

    /// <summary>One command of a plug-in: a button, a pinned chip, a search result or a shortcut target.</summary>
    public sealed class PanelAction : Observable
    {
        public PanelAction(PluginCard plugin, ManifestAction source, bool isCustom)
        {
            Plugin = plugin;
            Id = source.Id;
            DefaultCaption = string.IsNullOrWhiteSpace(source.Caption) ? source.Id : source.Caption.Trim();
            _caption = DefaultCaption;
            Tooltip = source.Tooltip;
            Kind = string.Equals(source.Kind, "command", StringComparison.OrdinalIgnoreCase) ? "command" : "macro";
            _command = source.Command ?? string.Empty;
            Image = source.Image;
            Group = source.Group;
            Featured = source.Featured;
            _isVisible = source.Featured;
            IsCustom = isCustom;
            Glyph = Glyphs.Pick(source.Image, DefaultCaption + " " + source.Command);
        }

        public PluginCard Plugin { get; }
        public string Id { get; }
        public string Key => Plugin.Name + "/" + Id;
        public string DefaultCaption { get; }
        public string Tooltip { get; }
        public string Kind { get; }
        public string Image { get; }
        public string Group { get; }
        public bool Featured { get; }
        public bool IsCustom { get; }
        public string Glyph { get; }

        private string _caption;
        public string Caption
        {
            get => _caption;
            set { if (Set(ref _caption, string.IsNullOrWhiteSpace(value) ? DefaultCaption : value)) Raise(nameof(ToolTipText)); }
        }

        private string _command;
        /// <summary>The CAF command key or the PML line; editable for your own commands only.</summary>
        public string Command
        {
            get => _command;
            set { if (Set(ref _command, value ?? string.Empty)) Raise(nameof(ToolTipText)); }
        }

        private bool _isVisible;
        /// <summary>Shown as a button on the card (the rest are under "More").</summary>
        public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

        private bool _isPinned;
        public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }

        private string _shortcut;
        public string Shortcut
        {
            get => _shortcut;
            set { if (Set(ref _shortcut, string.IsNullOrWhiteSpace(value) ? null : value)) { Raise(nameof(HasShortcut)); Raise(nameof(ToolTipText)); } }
        }

        public bool HasShortcut => !string.IsNullOrEmpty(_shortcut);

        private bool _isRecording;
        /// <summary>The shortcut button waits for the key combination.</summary>
        public bool IsRecording { get => _isRecording; set => Set(ref _isRecording, value); }

        public string ToolTipText
        {
            get
            {
                string text = string.IsNullOrWhiteSpace(Tooltip) ? Caption : Tooltip;
                if (HasShortcut) text += "\n" + Shortcut;
                return text + "\n" + (Kind == "command" ? "Command: " : "PML: ") + Command;
            }
        }

        /// <summary>Text matched by search.</summary>
        public string SearchText => Caption + " " + DefaultCaption + " " + Tooltip + " " + Plugin.DisplayName + " " + Plugin.Name + " " + Group;
    }

    /// <summary>One enabled plug-in on the panel.</summary>
    public sealed class PluginCard : Observable
    {
        private static readonly string[] Palette = { "#0891B2", "#7C3AED", "#DB2777", "#EA580C", "#16A34A", "#2563EB", "#CA8A04", "#0D9488", "#9333EA", "#DC2626" };

        public PluginCard(ManifestPlugin source)
        {
            Name = source.Name;
            DisplayName = string.IsNullOrWhiteSpace(source.DisplayName) ? source.Name : source.DisplayName;
            Description = source.Description;
            Path = source.Path;
            DefaultMode = PanelModes.Normalize(source.Mode);
            _mode = DefaultMode;
            Addins = source.Addins ?? new List<string>();
            PmlPaths = source.PmlPaths ?? new List<string>();
            int hash = 0;
            foreach (char c in Name.ToUpperInvariant()) hash = unchecked(hash * 31 + c);
            AvatarColor = Palette[Math.Abs(hash % Palette.Length)];
        }

        public string Name { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
        public string Path { get; }
        public string DefaultMode { get; }
        public List<string> Addins { get; }
        public List<string> PmlPaths { get; }
        public string AvatarColor { get; }

        public string Initials
        {
            get
            {
                var words = DisplayName.Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length >= 2) return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant();
                return DisplayName.Length <= 2 ? DisplayName.ToUpperInvariant() : DisplayName.Substring(0, 2).ToUpperInvariant();
            }
        }

        public ObservableCollection<PanelAction> Actions { get; } = new ObservableCollection<PanelAction>();

        private string _mode;
        public string Mode
        {
            get => _mode;
            set
            {
                if (!Set(ref _mode, PanelModes.Normalize(value))) return;
                Raise(nameof(IsButtons)); Raise(nameof(IsStatusOnly)); Raise(nameof(IsHidden));
                Refresh();
            }
        }

        public bool IsButtons { get => _mode == PanelModes.Buttons; set { if (value) Mode = PanelModes.Buttons; } }
        public bool IsStatusOnly { get => _mode == PanelModes.Status; set { if (value) Mode = PanelModes.Status; } }
        public bool IsHidden { get => _mode == PanelModes.Hidden; set { if (value) Mode = PanelModes.Hidden; } }

        private bool _isCollapsed;
        public bool IsCollapsed { get => _isCollapsed; set => Set(ref _isCollapsed, value); }

        private bool _showMore;
        public bool ShowMore { get => _showMore; set => Set(ref _showMore, value); }

        private bool _isAdding;
        /// <summary>The "add your own command" form is open (customize mode).</summary>
        public bool IsAdding { get => _isAdding; set => Set(ref _isAdding, value); }

        private PluginState _state = PluginState.Enabled;
        public PluginState State { get => _state; private set => Set(ref _state, value); }

        private string _stateText = "Enabled";
        public string StateText { get => _stateText; private set => Set(ref _stateText, value); }

        private string _stateDetail;
        public string StateDetail { get => _stateDetail; private set => Set(ref _stateDetail, value); }

        public void SetState(PluginState state, string text, string detail)
        {
            State = state;
            StateText = text;
            StateDetail = detail;
        }

        /// <summary>Buttons on the card.</summary>
        public IReadOnlyList<PanelAction> ButtonActions { get; private set; } = Array.Empty<PanelAction>();
        /// <summary>The rest, under "More".</summary>
        public IReadOnlyList<PanelAction> MoreActions { get; private set; } = Array.Empty<PanelAction>();
        public int MoreCount => MoreActions.Count;
        public bool HasActions => Actions.Count > 0;
        /// <summary>Columns of the button grid: one for a single button, two otherwise.</summary>
        public int ButtonColumns => ButtonActions.Count <= 1 ? 1 : 2;

        public void Refresh()
        {
            ButtonActions = IsButtons ? Actions.Where(a => a.IsVisible).ToList() : (IReadOnlyList<PanelAction>)Array.Empty<PanelAction>();
            MoreActions = IsButtons ? Actions.Where(a => !a.IsVisible).ToList() : (IReadOnlyList<PanelAction>)Array.Empty<PanelAction>();
            if (MoreActions.Count == 0) ShowMore = false;
            Raise(nameof(ButtonActions)); Raise(nameof(MoreActions)); Raise(nameof(MoreCount));
            Raise(nameof(HasActions)); Raise(nameof(ButtonColumns));
        }
    }
}
