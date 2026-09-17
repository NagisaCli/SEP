using System;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace SEP.App.Services;

/// <summary>Transient notifications at the bottom of the main window (WPF-UI snackbar), safe to call from any thread.</summary>
public sealed class ToastService
{
    private readonly ISnackbarService _snackbar = new SnackbarService();

    public void Attach(SnackbarPresenter presenter) => _snackbar.SetSnackbarPresenter(presenter);

    public void Success(string message, string? title = null) => Show(title, message, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
    public void Info(string message, string? title = null) => Show(title, message, ControlAppearance.Secondary, SymbolRegular.Info24);
    public void Warning(string message, string? title = null) => Show(title, message, ControlAppearance.Caution, SymbolRegular.Warning24);
    public void Error(string message, string? title = null) => Show(title, message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);

    /// <summary>Success or error depending on the outcome flag.</summary>
    public void Result(bool ok, string message)
    {
        if (ok) Success(message); else Error(message);
    }

    private void Show(string? title, string message, ControlAppearance appearance, SymbolRegular symbol)
    {
        void Do()
        {
            try
            {
                _snackbar.Show(title ?? string.Empty, message, appearance, new SymbolIcon(symbol), TimeSpan.FromSeconds(appearance == ControlAppearance.Danger ? 8 : 4));
            }
            catch (Exception ex)
            {
                App.Log($"toast: {ex.Message} | {message}");
            }
        }
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Do(); else d.BeginInvoke(Do);
    }
}
