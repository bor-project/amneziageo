namespace AmneziaGeo.Windows.App;

/// <summary>
/// The time a connect attempt gives the tunnel process to come up and the server to answer it.
/// </summary>
internal sealed class ConnectWindow(DateTimeOffset start, TimeSpan launch, TimeSpan silence)
{
    /// <summary>
    /// When the attempt started the tunnel process.
    /// </summary>
    public DateTimeOffset Started => start;

    /// <summary>
    /// When the tunnel process first answered; null before it has.
    /// </summary>
    public DateTimeOffset? AnsweredAt { get; private set; }

    /// <summary>
    /// Records an answer of the tunnel process.
    /// </summary>
    public void Answer(DateTimeOffset now)
    {
        AnsweredAt ??= now;
    }

    /// <summary>
    /// Whether the server stayed silent through the whole window since the tunnel process first answered.
    /// </summary>
    public bool Silent(DateTimeOffset now)
    {
        return AnsweredAt is { } answered && now - answered >= silence;
    }

    /// <summary>
    /// Whether the attempt ran out of time.
    /// </summary>
    public bool Over(DateTimeOffset now)
    {
        return now >= End();
    }

    // The launch deadline, moved out to the end of the server's window.
    private DateTimeOffset End()
    {
        var launched = start + launch;
        return AnsweredAt is { } answered && answered + silence > launched ? answered + silence : launched;
    }
}
