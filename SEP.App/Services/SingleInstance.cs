using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SEP.App.Services;

/// <summary>
/// One SEP window per Windows session and data folder. The first instance owns a named mutex and waits on a named
/// event; a later start signals that event (so the running window comes to the front) and exits. Portable test
/// copies use a different data folder and therefore never collide with the user's own instance.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, Action onActivate)
    {
        _mutex = mutex;
        _activate = activate;
        _wait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>The owning handle, or null when another instance already runs (it has been asked to show itself).</summary>
    /// <param name="waitForPrevious">
    /// Used right after an install/update hand-over: wait (up to 15 s) for the previous window to close instead of
    /// activating it.
    /// </param>
    public static SingleInstance? TryAcquire(string dataDir, Action onActivate, bool waitForPrevious = false)
    {
        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(dataDir.ToLowerInvariant())))[..16];
        string mutexName = $@"Local\SEP.App.{key}";
        string eventName = $@"Local\SEP.App.{key}.Activate";

        Mutex mutex;
        bool owned;
        try
        {
            mutex = new Mutex(initiallyOwned: true, mutexName, out owned);
        }
        catch (Exception ex)
        {
            // Never let the guard itself stop SEP from starting.
            App.Log($"Single-instance mutex unavailable ({ex.Message}); continuing without it");
            return new SingleInstance(new Mutex(), new EventWaitHandle(false, EventResetMode.AutoReset), onActivate);
        }

        if (!owned && waitForPrevious)
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { owned = true; }   // the previous instance ended without releasing
        }

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        if (!owned)
        {
            try { activate.Set(); } catch { }
            activate.Dispose();
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, activate, onActivate);
    }

    public void Dispose()
    {
        try { _wait.Unregister(null); } catch { }
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
        _activate.Dispose();
    }
}
