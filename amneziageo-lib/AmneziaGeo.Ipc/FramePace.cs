namespace AmneziaGeo.Ipc;

/// <summary>
/// Когда окну на экране нужны кадры.
/// </summary>
public static class FramePace
{
    /// <summary>
    /// Столько кадры идут после последнего изменения окна.
    /// </summary>
    public const int RestAfterMs = 150;

    /// <summary>
    /// Пауза между кадрами неподвижного окна.
    /// </summary>
    public const int BeatMs = 1_000;

    /// <summary>
    /// Пора ли остановить кадры окна.
    /// </summary>
    public static bool Rests(long now, long busyAt) => now - busyAt >= RestAfterMs;
}
