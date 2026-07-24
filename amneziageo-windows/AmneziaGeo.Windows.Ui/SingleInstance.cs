using System.Diagnostics;
using System.Threading;
using AmneziaGeo.Dal;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AmneziaGeo.Windows.Ui;

/// <summary>
/// Restricts the desktop UI to a single running instance per interactive session. The first instance owns
/// a named mutex and listens on a named event; a later launch signals that event and exits once the owner
/// confirms the window is up. An owner that never confirms - a hung or windowless --update/--apply worker -
/// no longer swallows the request: the launch raises the window already on screen, or opens one itself (#209).
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
    // Set by the owning instance once an activation has actually surfaced the window.
    private const string ActivatedEventName = @"Local\AmneziaGeo.Ui.Activated";

    // How long a launch waits for that confirmation before opening a window itself, and how often it looks.
    private static readonly TimeSpan _activationTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan _activationPoll = TimeSpan.FromMilliseconds(200);

    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;
    private static EventWaitHandle? _update;
    private static EventWaitHandle? _apply;
    private static EventWaitHandle? _activated;
    private static RegisteredWaitHandle? _registration;
    private static RegisteredWaitHandle? _updateRegistration;
    private static RegisteredWaitHandle? _applyRegistration;

    /// <summary>
    /// Whether this process owns the session mutex. False in an instance the activation watchdog forced up,
    /// which must claim neither the shared names nor the assumptions that hang off single instance.
    /// </summary>
    public static bool OwnsSession { get; private set; }

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
    /// Returns true when the caller should continue starting: either as the only instance, or as the second one
    /// the watchdog forced because the running instance never surfaced a window. Returns false when another
    /// instance took the request over (surfaced its window, or was handed an update / install relay).
    /// </summary>
    public static bool TryAcquire(bool requestUpdate = false, bool requestApply = false)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            OwnsSession = true;
            return true;
        }

        // A relay carries no window, so there is nothing to wait for.
        if (requestUpdate || requestApply)
        {
            var relay = requestApply ? ApplyEventName : UpdateEventName;
            ClientLog.Info($"another GUI instance owns the session ({OwnerPids()}): relaying {relay}");
            SignalExistingInstance(relay);
            return false;
        }

        ClientLog.Info($"another GUI instance owns the session ({OwnerPids()}): asking it to surface the window");
        return !HandOverActivation();
    }

    // Nudges the running instance and waits for it to confirm the window, falling back to raising a window that
    // is on screen anyway. Returns whether the request was served.
    private static bool HandOverActivation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        using var acknowledged = OpenAcknowledgement();
        // Clear a confirmation left over from a round whose launch had already timed out.
        acknowledged?.Reset();
        SignalExistingInstance(ActivateEventName);

        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < _activationTimeout)
        {
            if (WaitForAcknowledgement(acknowledged, _activationPoll))
            {
                ClientLog.Info("the running instance surfaced its window");
                return true;
            }
        }

        if (RaiseWindowOfRunningInstance())
        {
            ClientLog.Warning($"no answer in {_activationTimeout.TotalSeconds:0} s: raised the GUI window already on screen");
            return true;
        }

        ClientLog.Warning($"no answer in {_activationTimeout.TotalSeconds:0} s and no GUI window: starting a second instance");
        return false;
    }

    // Waits out one poll interval; returns whether the running instance confirmed within it.
    private static bool WaitForAcknowledgement(EventWaitHandle? acknowledged, TimeSpan interval)
    {
        if (acknowledged is null)
        {
            Thread.Sleep(interval);
            return false;
        }

        return acknowledged.WaitOne(interval);
    }

    // Brings up the window of another GUI process, for an owner that has one but never answered (still starting,
    // so its activation wait was not armed when the signal arrived).
    private static bool RaiseWindowOfRunningInstance()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("AmneziaGeo.Windows.Ui"))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == 0)
                    {
                        continue;
                    }

                    ForegroundWindow.Raise(process.MainWindowHandle);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            ClientLog.Error("could not look for a running GUI window", ex);
        }

        return false;
    }

    // Process ids of the other GUI instances, so the journal names who the request went to.
    private static string OwnerPids()
    {
        try
        {
            var pids = new List<int>();
            foreach (var process in Process.GetProcessesByName("AmneziaGeo.Windows.Ui"))
            {
                using (process)
                {
                    if (process.Id != Environment.ProcessId)
                    {
                        pids.Add(process.Id);
                    }
                }
            }

            return pids.Count == 0 ? "pid unknown" : "pid " + string.Join(", ", pids);
        }
        catch (Exception)
        {
            return "pid unknown";
        }
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
            else
            {
                ClientLog.Warning($"the running instance is not listening on {eventName}");
            }
        }
        catch (Exception ex)
        {
            ClientLog.Error($"could not signal {eventName}", ex);
        }
    }

    private static EventWaitHandle? OpenAcknowledgement()
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, ActivatedEventName);
        }
        catch (Exception ex)
        {
            ClientLog.Error("could not open the activation acknowledgement", ex);
            return null;
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
        // A forced second instance does not own the names: listening on them would steal signals the owner still
        // answers. Later launches reach this instance through the watchdog's raise fallback instead.
        if (!OwnsSession)
        {
            return;
        }

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activate,
            (_, _) => Dispatcher.UIThread.Post(() =>
            {
                (ActivationHandler ?? BringMainWindowToFront).Invoke();
                ConfirmActivation();
            }),
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

    // Tells the waiting launch the window is up, so its watchdog does not open a second one.
    private static void ConfirmActivation()
    {
        try
        {
            _activated ??= new EventWaitHandle(false, EventResetMode.AutoReset, ActivatedEventName);
            _activated.Set();
            ClientLog.Info("activation served: the window is up");
        }
        catch (Exception ex)
        {
            ClientLog.Error("could not confirm the activation", ex);
        }
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
