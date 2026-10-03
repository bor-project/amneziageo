namespace AmneziaGeo.Ipc;

/// <summary>
/// What one attempt to raise a session ended with.
/// </summary>
/// <param name="Up">Whether the session stands.</param>
/// <param name="Reason">Why it does not.</param>
/// <param name="Detail">The cause in a few words.</param>
public readonly record struct DialOutcome(bool Up, ConnectFailureReason Reason, string Detail)
{
    /// <summary>
    /// The session stands.
    /// </summary>
    public static DialOutcome Raised { get; } = new(true, ConnectFailureReason.Unknown, string.Empty);

    /// <summary>
    /// The attempt failed on the cause given.
    /// </summary>
    public static DialOutcome Failed(ConnectFailureReason reason, string detail = "") => new(false, reason, detail);
}

/// <summary>
/// What a dial is made of on a platform.
/// </summary>
/// <param name="OnNetwork">Whether the device sits on a network.</param>
/// <param name="Attempt">Raises the session once.</param>
/// <param name="Pause">Waits the time given; a change of the network ends it sooner.</param>
/// <param name="Held">Told when the dial starts waiting for a network.</param>
/// <param name="Released">Told when the network it waited for is there.</param>
/// <param name="Retrying">Told before each pause: failures in a row, the pause, what the attempt ended with.</param>
public sealed record DialSteps(
    Func<bool> OnNetwork,
    Func<CancellationToken, Task<DialOutcome>> Attempt,
    Func<TimeSpan, CancellationToken, Task> Pause,
    Action? Held = null,
    Action? Released = null,
    Action<int, TimeSpan, DialOutcome>? Retrying = null)
{
    /// <summary>
    /// Time between two looks at the network while the dial waits for one.
    /// </summary>
    public TimeSpan NetworkLook { get; init; } = TimeSpan.FromSeconds(ConnectRetry.NetworkLookSeconds);
}

/// <summary>
/// Keeps a connect dialled until the session stands.
/// </summary>
public static class ConnectRetry
{
    /// <summary>
    /// Longest pause between attempts where no retry interval is set, in seconds.
    /// </summary>
    public const int CeilingSeconds = 60;

    /// <summary>
    /// Seconds between two looks at the network while a dial waits for one.
    /// </summary>
    public const int NetworkLookSeconds = 60;

    // Pauses after the first four failures in a row.
    private static readonly int[] _steps = [0, 5, 10, 20];

    /// <summary>
    /// Whether another attempt gets past the cause.
    /// </summary>
    public static bool IsTransient(ConnectFailureReason reason) => reason switch
    {
        ConnectFailureReason.NoHandshake or ConnectFailureReason.UnderlayUnreachable
            or ConnectFailureReason.Timeout or ConnectFailureReason.ServiceLaunchFailed
            or ConnectFailureReason.EngineStopped or ConnectFailureReason.Unknown => true,
        _ => false,
    };

    /// <summary>
    /// The pause before the next attempt, capped by the retry interval while auto-reconnect is on.
    /// </summary>
    public static TimeSpan Delay(int attempt, bool periodic, int intervalSeconds)
    {
        var ceiling = periodic && intervalSeconds > 0 ? intervalSeconds : CeilingSeconds;
        var index = Math.Max(attempt, 1) - 1;
        var step = index < _steps.Length ? _steps[index] : ceiling;
        return TimeSpan.FromSeconds(Math.Min(step, ceiling));
    }

    /// <summary>
    /// Dials until the session stands or fails on a cause another attempt does not get past; null when the dial
    /// is taken back.
    /// </summary>
    public static async Task<DialOutcome?> RunAsync(DialSteps steps, CancellationToken ct)
    {
        try
        {
            for (var failures = 1; ; failures++)
            {
                await HoldAsync(steps, ct).ConfigureAwait(false);
                var outcome = await steps.Attempt(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (outcome.Up || !IsTransient(outcome.Reason))
                {
                    return outcome;
                }

                var delay = Delay(failures, false, 0);
                steps.Retrying?.Invoke(failures, delay, outcome);
                await steps.Pause(delay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // Waits until the device sits on a network.
    private static async Task HoldAsync(DialSteps steps, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (steps.OnNetwork())
        {
            return;
        }

        steps.Held?.Invoke();
        do
        {
            await steps.Pause(steps.NetworkLook, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        while (!steps.OnNetwork());

        steps.Released?.Invoke();
    }
}
