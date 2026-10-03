namespace AmneziaGeo.Ipc;

/// <summary>
/// What the tunnel said about the server of the configuration a connect names while another one is connected.
/// </summary>
public static class SwitchVerdict
{
    /// <summary>
    /// The server answered the handshake.
    /// </summary>
    public const string Answered = "answered";

    /// <summary>
    /// The server kept silent for the whole wait.
    /// </summary>
    public const string Silent = "silent";

    /// <summary>
    /// The tunnel could not tell.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The verdict for what the engine told: true answered, false silent, null neither.
    /// </summary>
    public static string Of(bool? answered) => answered switch
    {
        true => Answered,
        false => Silent,
        _ => Unknown,
    };
}

/// <summary>
/// Keeps a connected tunnel while the server of the configuration a connect names has not answered.
/// </summary>
public static class SwitchGuard
{
    /// <summary>
    /// The configuration a connected tunnel stands on when the connect names another one; null when there is
    /// nothing to keep.
    /// </summary>
    public static string? Standing(bool active, string? status, string? bound, string target)
    {
        return active
            && string.Equals(status, ConnectionStatus.Connected, StringComparison.Ordinal)
            && bound is { Length: > 0 }
            && !string.Equals(bound, target, StringComparison.Ordinal)
                ? bound
                : null;
    }

    /// <summary>
    /// Whether the tunnel that stands is kept: only a server that kept silent refuses the switch.
    /// </summary>
    public static bool Keeps(string? verdict)
    {
        return string.Equals(verdict?.Trim(), SwitchVerdict.Silent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Waits for the word of the tunnel; unknown when none comes inside the wait.
    /// </summary>
    public static async Task<string> AwaitAsync(Func<string> read, int waitMs, int pollMs, Func<int, Task> pause)
    {
        for (var waited = 0; waited < waitMs; waited += pollMs)
        {
            await pause(pollMs).ConfigureAwait(false);
            var said = read().Trim();
            if (said.Length > 0)
            {
                return said;
            }
        }

        return SwitchVerdict.Unknown;
    }
}
