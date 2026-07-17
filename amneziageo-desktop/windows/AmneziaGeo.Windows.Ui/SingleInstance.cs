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
    // Signalled by a later "--update" launch (tray menu / balloon) so the running instance starts the update.
    private const string UpdateEventName = @"Local\AmneziaGeo.Ui.Update";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;
    private static EventWaitHandle? _update;
    private static RegisteredWaitHandle? _registration;
    private static RegisteredWaitHandle? _updateRegistration;

    /// <summary>
    /// Invoked on the UI thread when a later launch nudges this instance to the foreground; falls back to
    /// surfacing the main window when unset.
    /// </summary>
    public static Action? ActivationHandler;

    /// <summary>
    /// Invoked on the UI thread when a later "--update" launch asks this instance to start the update flow.
    /// </summary>
    public static Action? UpdateHandler;

    /// <summary>
    /// Returns true when this is the only instance and the caller should continue starting. Returns
    /// false when another instance already runs and has been asked to surface its window or start an update.
    /// </summary>
    public static bool TryAcquire(bool requestUpdate = false)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance(requestUpdate ? UpdateEventName : ActivateEventName);
            return false;
        }

        return true;
    }

    // Another instance owns the mutex: nudge it (surface its window, or start an update), then exit.
    private static void SignalExistingInstance(string eventName)
    {
        // Guard to satisfy the platform analyzer (TryOpenExisting is Windows-only).
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (EventWaitHandle.TryOpenExisting(eventName, out var existing))
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

    /// <summary>
    /// Owning instance: start listening for a later launch's activation and update signals. Called from the UI
    /// thread once Avalonia is initialized - arming these waits earlier lets an incoming signal touch
    /// Dispatcher.UIThread from a threadpool thread before the platform dispatcher exists, which caches a
    /// null-backed dispatcher and breaks startup.
    /// </summary>
    public static void StartListening()
    {
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activate,
            (_, _) => Dispatcher.UIThread.Post(() => (ActivationHandler ?? BringMainWindowToFront).Invoke()),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        _update = new EventWaitHandle(false, EventResetMode.AutoReset, UpdateEventName);
        _updateRegistration = ThreadPool.RegisterWaitForSingleObject(
            _update,
            (_, _) => Dispatcher.UIThread.Post(() => UpdateHandler?.Invoke()),
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
