namespace AmneziaGeo.Ipc;

/// <summary>
/// Как редко голова работает, пока её окна нет на экране.
/// </summary>
public static class HeadPace
{
    /// <summary>
    /// Пауза между взглядами на процесс туннеля с окном на экране.
    /// </summary>
    public const int ShownWatchMs = 3_000;

    /// <summary>
    /// Пауза между взглядами на процесс туннеля без окна на экране.
    /// </summary>
    public const int HiddenWatchMs = 30_000;

    /// <summary>
    /// Пауза между снимками состояния по показаниям связи без окна на экране.
    /// </summary>
    public const long HiddenLinkMs = 60_000;

    /// <summary>
    /// Пауза до следующего взгляда на процесс туннеля.
    /// </summary>
    public static int WatchMs(bool shown) => shown ? ShownWatchMs : HiddenWatchMs;

    /// <summary>
    /// Пора ли собирать снимок состояния по показанию связи.
    /// </summary>
    public static bool TellsLink(bool shown, long now, long toldAt) => shown || now - toldAt >= HiddenLinkMs;
}
