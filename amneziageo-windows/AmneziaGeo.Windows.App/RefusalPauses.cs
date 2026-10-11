namespace AmneziaGeo.Windows.App;

/// <summary>
/// Counts the refusals of a websocket front in a row and says how long the carrier waits before it asks again.
/// </summary>
internal sealed class RefusalPauses
{
    /// <summary>
    /// Life of a carrier past which a refusal starts the count anew: one that lived this long had its websocket.
    /// </summary>
    public const int SettledMs = 120_000;

    private static readonly int[] _seconds = [1, 1, 1, 2, 3, 5, 8, 15, 30];

    /// <summary>
    /// Refusals in a row.
    /// </summary>
    public int InRow { get; private set; }

    /// <summary>
    /// Counts the refusal of a carrier and returns the wait before the next one is started.
    /// </summary>
    public TimeSpan Next(bool carried, long livedMs)
    {
        if (carried || livedMs >= SettledMs)
        {
            InRow = 0;
        }

        InRow++;
        return Of(InRow);
    }

    /// <summary>
    /// Returns the wait after the given number of refusals in a row.
    /// </summary>
    public static TimeSpan Of(int inRow)
    {
        return TimeSpan.FromSeconds(_seconds[Math.Clamp(inRow, 1, _seconds.Length) - 1]);
    }
}
