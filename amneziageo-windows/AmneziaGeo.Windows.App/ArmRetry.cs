namespace AmneziaGeo.Windows.App;

/// <summary>
/// Repeats the arming of the leak protection.
/// </summary>
internal static class ArmRetry
{
    /// <summary>
    /// Arms until it takes or the attempts run out and says how many were made; a cancelled session ends with
    /// the cancellation.
    /// </summary>
    public static async Task<(bool Armed, int Attempts)> RunAsync(Func<bool> arm, int attempts, TimeSpan delay, Action<int> retrying, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (arm())
            {
                return (true, attempt);
            }

            ct.ThrowIfCancellationRequested();
            if (attempt >= attempts)
            {
                return (false, attempt);
            }

            retrying(attempt);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}
