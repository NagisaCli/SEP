using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using SEP.App.Resources;
using SEP.App.Services;
using Wpf.Ui.Controls;

namespace SEP.App.Views.Dialogs;

/// <summary>Generic text / password / choice prompt driven by a <see cref="PromptRequest"/>.</summary>
public partial class PromptDialog : FluentWindow
{
    private readonly PromptState _state;

    public PromptDialog(PromptRequest request)
    {
        _state = new PromptState(request);
        DataContext = _state;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_state.HasChoices && ChoiceBox.Items.Count > 0) ChoiceBox.Focus();
            else if (_state.HasText) TextField.Focus();
            else if (_state.HasPassword) PasswordField.Focus();
            else OkButton.Focus();
        };
    }

    public PromptResult? Result { get; private set; }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var r = _state.Request;
        if (r.PasswordLabel != null)
        {
            if (_state.Password.Length == 0) { _state.Error = Strings.Users_PasswordEmpty; return; }
            if (_state.Password != _state.PasswordRepeat) { _state.Error = Strings.Users_PasswordMismatch; return; }
        }
        if (r.TextLabel != null && r.ChoiceLabel == null && _state.Text.Trim().Length == 0)
        {
            _state.Error = Strings.Common_ValueRequired;
            return;
        }
        Result = new PromptResult(_state.Text.Trim(), _state.Password, _state.Choice);
        DialogResult = true;
        Close();
    }

    private sealed partial class PromptState : ObservableObject
    {
        public PromptState(PromptRequest request)
        {
            Request = request;
            _text = request.TextInitial ?? string.Empty;
            _choice = request.ChoiceInitial;
        }

        public PromptRequest Request { get; }
        public bool HasMessage => !string.IsNullOrWhiteSpace(Request.Message);
        public bool HasText => Request.TextLabel != null;
        public bool HasPassword => Request.PasswordLabel != null;
        public bool HasChoices => Request.ChoiceLabel != null && Request.Choices is { Count: > 0 };
        public CharacterCasing Casing => Request.TextUpperCase ? CharacterCasing.Upper : CharacterCasing.Normal;
        public ControlAppearance OkAppearance => Request.Danger ? ControlAppearance.Danger : ControlAppearance.Primary;

        [ObservableProperty] private string _text;
        [ObservableProperty] private string _password = string.Empty;
        [ObservableProperty] private string _passwordRepeat = string.Empty;
        [ObservableProperty] private string? _choice;
        [ObservableProperty] private string _error = string.Empty;
    }
}
