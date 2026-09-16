using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using SEP.App.Resources;
using SEP.App.Views.Dialogs;
using Wpf.Ui.Controls;

namespace SEP.App.Services;

/// <summary>WPF implementation of <see cref="IDialogService"/>: WPF-UI message boxes and the small prompt windows.</summary>
public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                                    ?? Application.Current?.MainWindow;

    public async Task<bool> ConfirmAsync(string title, string message, string okLabel, bool danger = false)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = okLabel,
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            CloseButtonText = Strings.Common_Cancel,
            Owner = Owner,
        };
        var result = await box.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            CloseButtonText = Strings.Common_Ok,
            Owner = Owner,
        };
        await box.ShowDialogAsync();
    }

    public Task<PromptResult?> PromptAsync(PromptRequest request)
    {
        var dlg = new PromptDialog(request) { Owner = Owner };
        bool ok = dlg.ShowDialog() == true;
        return Task.FromResult(ok ? dlg.Result : null);
    }

    public Task<UserDraft?> NewUserAsync(IReadOnlyList<string> teams, string projectCode)
    {
        var dlg = new NewUserDialog(teams, projectCode) { Owner = Owner };
        bool ok = dlg.ShowDialog() == true;
        return Task.FromResult(ok ? dlg.Result : null);
    }
}
