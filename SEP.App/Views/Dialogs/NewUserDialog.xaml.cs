using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SEP.App.Resources;
using SEP.App.Services;
using Wpf.Ui.Controls;

namespace SEP.App.Views.Dialogs;

public partial class NewUserDialog : FluentWindow
{
    private static readonly Regex NameRe = new(@"^[A-Za-z0-9_]{1,32}$", RegexOptions.Compiled);
    private readonly State _state;

    public NewUserDialog(IReadOnlyList<string> teams, string projectCode)
    {
        _state = new State(teams, projectCode);
        DataContext = _state;
        InitializeComponent();
        Loaded += (_, _) => NameField.Focus();
    }

    public UserDraft? Result { get; private set; }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string name = _state.Name.Trim().ToUpperInvariant();
        if (!NameRe.IsMatch(name)) { _state.Error = Strings.Users_NameInvalid; return; }
        if (_state.Password != _state.PasswordRepeat) { _state.Error = Strings.Users_PasswordMismatch; return; }
        if (_state.Password.Any(c => char.IsWhiteSpace(c) || c is '|' or '\'' or '"')) { _state.Error = Strings.Users_PasswordRules; return; }

        string? team = (_state.Team ?? _state.TeamText)?.Trim().TrimStart('*');
        if (string.IsNullOrEmpty(team) || team == Strings.Users_NoTeamOption) team = null;
        else if (!NameRe.IsMatch(team)) { _state.Error = Strings.Users_TeamNameInvalid; return; }

        Result = new UserDraft(name, _state.IsFree ? "Free" : "General",
            _state.Password.Length == 0 ? null : _state.Password,
            string.IsNullOrWhiteSpace(_state.Description) ? null : _state.Description.Trim(),
            team?.ToUpperInvariant());
        DialogResult = true;
        Close();
    }

    private sealed partial class State : ObservableObject
    {
        public State(IReadOnlyList<string> teams, string projectCode)
        {
            Title = string.Format(Strings.Users_NewTitle, projectCode);
            var choices = new List<string> { Strings.Users_NoTeamOption };
            choices.AddRange(teams);
            TeamChoices = choices;
            _team = choices.Count > 1 ? choices[1] : choices[0];
            _teamText = _team;
        }

        public string Title { get; }
        public List<string> TeamChoices { get; }
        public string SecurityHint => IsFree ? Strings.Users_SecurityFreeHint : Strings.Users_SecurityGeneralHint;

        [ObservableProperty] private string _name = string.Empty;
        [ObservableProperty] private string _password = string.Empty;
        [ObservableProperty] private string _passwordRepeat = string.Empty;
        [ObservableProperty] private string _description = string.Empty;
        [ObservableProperty] private string? _team;
        [ObservableProperty] private string _teamText;
        [ObservableProperty] private string _error = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsGeneral))]
        [NotifyPropertyChangedFor(nameof(SecurityHint))]
        private bool _isFree;

        public bool IsGeneral
        {
            get => !IsFree;
            set => IsFree = !value;
        }
    }
}
