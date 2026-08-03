namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Closes the shell the UI runs in.
/// </summary>
internal delegate void AppExitRequest();

/// <summary>
/// Runtime registry of the platform exit. A host registers one where the shell has no window chrome to close
/// it with (Android TV); the UI then offers its own exit control. A running tunnel is not touched: it lives in
/// its own foreground service.
/// </summary>
internal static class AppExitHost
{
    private static AppExitRequest? _exit;

    /// <summary>
    /// Whether this platform supplies an in-app exit.
    /// </summary>
    public static bool IsAvailable => _exit is not null;

    /// <summary>
    /// Registers the platform exit.
    /// </summary>
    public static void Register(AppExitRequest exit) => _exit = exit;

    /// <summary>
    /// Closes the shell, or does nothing when the platform registered none.
    /// </summary>
    public static void Exit() => _exit?.Invoke();
}
