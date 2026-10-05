using System;
using System.Collections.Generic;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Что система решает о работе приложения в фоне.
/// </summary>
internal enum BackgroundLimitKind
{
    Notifications,
    Battery,
    Autostart,
}

/// <summary>
/// Состояние ограничения фона.
/// </summary>
internal enum BackgroundLimitState
{
    Unknown,
    Free,
    Limited,
}

/// <summary>
/// Ограничение фона и его состояние.
/// </summary>
internal readonly record struct BackgroundLimit(BackgroundLimitKind Kind, BackgroundLimitState State)
{
    /// <summary>
    /// Экономия батареи по списку исключений системы и по тому, чей экран о нём спрашивает.
    /// </summary>
    public static BackgroundLimit Battery(bool exempt, bool vendorScreen)
    {
        return new(BackgroundLimitKind.Battery, (exempt, vendorScreen) switch
        {
            (true, _) => BackgroundLimitState.Free,
            (false, true) => BackgroundLimitState.Unknown,
            _ => BackgroundLimitState.Limited,
        });
    }
}

/// <summary>
/// Hook for the background limits of the platform. The host (Android) registers a reader of the limits the device
/// has and an opener of the system screen of each. No registration means the platform has none.
/// </summary>
internal static class BackgroundLimitsBridge
{
    private static Func<IReadOnlyList<BackgroundLimit>>? _read;
    private static Action<BackgroundLimitKind>? _open;

    /// <summary>
    /// Регистрирует чтение ограничений и открытие их системных экранов.
    /// </summary>
    public static void Register(Func<IReadOnlyList<BackgroundLimit>> read, Action<BackgroundLimitKind> open)
    {
        _read = read;
        _open = open;
    }

    /// <summary>
    /// Называет ограничения устройства и состояние каждого.
    /// </summary>
    public static IReadOnlyList<BackgroundLimit> Read() => _read?.Invoke() ?? [];

    /// <summary>
    /// Открывает системный экран ограничения.
    /// </summary>
    public static void Open(BackgroundLimitKind kind) => _open?.Invoke(kind);
}
