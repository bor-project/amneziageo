using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Restricts the desktop UI to a single running instance per interactive session. The first instance owns
/// a named mutex and listens on a named event; a later launch signals that event - bringing the existing
/// window to the front - and exits without opening a second window.
/// </summary>
internal static class SingleInstance
{
    // Per-session (Local\) names: one UI per user session, which is what the user sees. A Global\ name
    // would also block a second logged-in user on the same machine, which is not the intent. Each handle is
    // held in a static field for the whole process lifetime so the GC cannot collect it and drop the lock.
    private const string MutexName = @"Local\AmneziaGeo.Ui.SingleInstance";
    private const string ActivateEventName = @"Local\AmneziaGeo.Ui.Activate";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;
    private static RegisteredWaitHandle? _registration;

    /// <summary>
    /// Returns true when this is the only instance and the caller should continue starting. Returns false
    /// when another instance already runs - it has been asked to surface its window and the caller must exit.
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

    // Another instance owns the mutex: nudge it to the foreground (best effort), then let the caller exit.
    private static void SignalExistingInstance()
    {
        // TryOpenExisting is annotated Windows-only; this app only runs on Windows, but the Ui project
        // targets plain net10.0 (Avalonia is cross-platform), so guard it to satisfy the platform analyzer.
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
            // Best effort: even if we cannot signal the first instance, we must still not open a window.
        }
    }

    // Owning instance: listen for a later launch's activation signal so the main window can be raised.
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
        // A brief Topmost flip is the reliable way to pull a window above the foreground app without keeping
        // it pinned on top.
        window.Topmost = true;
        window.Topmost = false;
    }
}
