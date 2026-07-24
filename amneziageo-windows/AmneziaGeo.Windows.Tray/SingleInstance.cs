namespace AmneziaGeo.Windows.Tray;

/// <summary>
/// One tray per interactive session. The first instance owns a named mutex and listens on a named event; a
/// later launch (the shortcut clicked again) signals the event so the running tray surfaces the GUI, then exits.
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\AmneziaGeo.Tray.SingleInstance";
    private const string ActivateEventName = @"Local\AmneziaGeo.Tray.Activate";
    // Signalled by the installer so an upgrade can retire the resident tray cleanly (icon removed, exe freed).
    private const string QuitEventName = @"Local\AmneziaGeo.Tray.Quit";

    // Held for the whole process lifetime so the GC cannot drop the lock.
    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;
    private static EventWaitHandle? _quit;
    private static RegisteredWaitHandle? _registration;
    private static RegisteredWaitHandle? _quitRegistration;

    /// <summary>
    /// Returns true when this is the only tray and startup should continue. Returns false when another tray
    /// already runs; <paramref name="signalled"/> then tells whether it was actually nudged to open the GUI -
    /// a tray that has not armed its wait yet cannot be, and the caller opens the GUI itself (#209).
    /// </summary>
    public static bool TryAcquire(out bool signalled)
    {
        signalled = false;
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            return true;
        }

        if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
        {
            using (existing)
            {
                existing.Set();
                signalled = true;
            }
        }

        return false;
    }

    /// <summary>
    /// Posts <paramref name="message"/> to <paramref name="hWnd"/> whenever a later launch signals activation.
    /// </summary>
    public static void ListenForActivation(nint hWnd, uint message)
    {
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activate,
            (_, _) => Native.PostMessageW(hWnd, message, 0, 0),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>
    /// Posts <paramref name="message"/> to <paramref name="hWnd"/> when the installer asks the tray to quit.
    /// </summary>
    public static void ListenForQuit(nint hWnd, uint message)
    {
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        _quitRegistration = ThreadPool.RegisterWaitForSingleObject(
            _quit,
            (_, _) => Native.PostMessageW(hWnd, message, 0, 0),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }
}
