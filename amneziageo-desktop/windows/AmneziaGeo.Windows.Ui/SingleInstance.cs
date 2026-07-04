using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Restricts the desktop UI to a single running instance per interactive session. The first instance owns
/// a named mutex and listens on a named event; a later launch signals that event and exits.
/// </summary>
internal static class SingleInstance
{
    // Per-session (Local\) names: one UI per user session. Handles held in static fields for the whole
    // process lifetime so the GC cannot collect them and drop the lock.
    private const string MutexName = @"Local\AmneziaGeo.Ui.SingleInstance";
    private const string ActivateEventName = @"Local\AmneziaGeo.Ui.Activate";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;
    private static RegisteredWaitHandle? _registration;

    /// <summary>
    /// Returns true when this is the only instance and the caller should continue starting. Returns
    /// false when another instance already runs and has been asked to surface its window.
    /// </summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            return false;
        }

        StartListening();
        return true;
    }

    // Another instance owns the mutex: nudge it to the foreground, then exit.
    private static void SignalExistingInstance()
    {
        // Guard to satisfy the platform analyzer (TryOpenExisting is Windows-only).
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
            {
                using (existing)
                {
                    existing.Set();
                }
            }
        }
        catch
        {
            // Best effort: do not open a window even if signalling fails.
        }
    }

    // Owning instance: listen for a later launch's activation signal.
    private static void StartListening()
    {
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activate,
            (_, _) => Dispatcher.UIThread.Post(BringMainWindowToFront),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    private static void BringMainWindowToFront()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
        // Brief Topmost flip to pull the window above the foreground app.
        window.Topmost = true;
        window.Topmost = false;
    }
}
