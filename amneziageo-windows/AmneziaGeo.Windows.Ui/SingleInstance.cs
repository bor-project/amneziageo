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
    // Signalled by a later "--update" launch (tray menu / balloon) so the running instance downloads the update.
    private const string UpdateEventName = @"Local\AmneziaGeo.Ui.Update";
    // Signalled by a later "--apply" launch (tray menu / balloon) so the running instance installs the download.
    private const string ApplyEventName = @"Local\AmneziaGeo.Ui.Apply";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;
    private static EventWaitHandle? _update;
    private static EventWaitHandle? _apply;
    private static RegisteredWaitHandle? _registration;
    private static RegisteredWaitHandle? _updateRegistration;
    private static RegisteredWaitHandle? _applyRegistration;

    /// <summary>
    /// Invoked on the UI thread when a later launch nudges this instance to the foreground; falls back to
    /// surfacing the main window when unset.
    /// </summary>
    public static Action? ActivationHandler;

    /// <summary>
    /// Invoked on the UI thread when a later "--update" launch asks this instance to download the update.
    /// </summary>
    public static Action? UpdateHandler;

    /// <summary>
    /// Invoked on the UI thread when a later "--apply" launch asks this instance to install the download.
    /// </summary>
    public static Action? ApplyHandler;

    /// <summary>
    /// Returns true when this is the only instance and the caller should continue starting. Returns
    /// false when another instance already runs and has been asked to surface its window, download an update,
    /// or install a downloaded one.
    /// </summary>
    public static bool TryAcquire(bool requestUpdate = false, bool requestApply = false)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            var eventName = requestApply ? ApplyEventName : requestUpdate ? UpdateEventName : ActivateEventName;
            SignalExistingInstance(eventName);
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

        _apply = new EventWaitHandle(false, EventResetMode.AutoReset, ApplyEventName);
        _applyRegistration = ThreadPool.RegisterWaitForSingleObject(
            _apply,
            (_, _) => Dispatcher.UIThread.Post(() => ApplyHandler?.Invoke()),
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
        ForegroundWindow.Raise(window);
    }
}
