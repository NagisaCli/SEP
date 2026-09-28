using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace SEP.App.ViewModels;

/// <summary>
/// Runs an action on the UI thread once per burst of requests. A scan, a batch edit or a group drag raises the
/// catalog's Changed event many times in a row (from any thread); the pages rebuild once, after the burst, at a
/// priority below input and rendering so the window stays responsive meanwhile.
/// </summary>
internal sealed class CoalescedUiAction
{
    private readonly Action _action;
    private int _queued;

    public CoalescedUiAction(Action action) => _action = action;

    public void Request()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            if (dispatcher == null) _action();
            return;
        }
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Volatile.Write(ref _queued, 0);
            _action();
        }));
    }
}
