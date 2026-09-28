namespace AmneziaGeo.Ipc;

/// <summary>
/// The tunnel and the networks under it at one moment, read as the reason a request through them failed.
/// </summary>
/// <param name="Tunnel">Stage of the tunnel as a <see cref="ConnectionStatus"/> word.</param>
/// <param name="HandshakeAgeSeconds">Seconds since the peer last answered; -1 before it ever has.</param>
/// <param name="RxBitsPerSecond">What the tunnel received over the last reading.</param>
/// <param name="LossPercent">Echoes lost inside the tunnel; <see cref="LinkHealth.LossUnknown"/> without a target.</param>
/// <param name="Churning">Whether the session keeps being re-established while nothing comes back.</param>
/// <param name="Under">The network the device sits on: Wi-Fi, mobile, ethernet, none; empty when not known.</param>
/// <param name="UnderValidated">Whether the system reaches the internet on that network; null when not known.</param>
/// <param name="TunnelValidated">Whether the system reaches the internet through the tunnel; null when not known.</param>
/// <param name="PrivateDnsHost">The host of a strict private DNS; null when none is set.</param>
/// <param name="PrivateDnsActive">Whether the system resolves over private DNS; null when not known.</param>
public sealed record NetworkSnapshot(
    string Tunnel,
    int HandshakeAgeSeconds,
    long RxBitsPerSecond,
    int LossPercent,
    bool Churning,
    string Under,
    bool? UnderValidated,
    bool? TunnelValidated,
    string? PrivateDnsHost,
    bool? PrivateDnsActive)
{
    /// <summary>
    /// Network word of a device on no network at all.
    /// </summary>
    public const string NoNetwork = "none";

    /// <summary>
    /// Whether the tunnel is up.
    /// </summary>
    public bool Up => string.Equals(Tunnel, ConnectionStatus.Connected, StringComparison.Ordinal);

    /// <summary>
    /// Whether a running tunnel carries nothing: the session is re-established over and over, has expired, or
    /// loses the echoes sent through it.
    /// </summary>
    public bool Dead => Up
        && (Churning
            || HandshakeAgeSeconds >= LinkRecovery.DefaultDeadHandshakeSeconds
            || (LinkHealth.LossKnown(LossPercent) && LossPercent >= LinkHealth.LostPercent));

    /// <summary>
    /// Where the failure lies.
    /// </summary>
    public string Verdict()
    {
        if (string.Equals(Under, NoNetwork, StringComparison.Ordinal))
        {
            return "the device has no network";
        }

        if (!Up)
        {
            return $"the tunnel is {Tunnel}";
        }

        if (Dead)
        {
            return "the tunnel carries nothing";
        }

        if (TunnelValidated == false && PrivateDnsHost is { Length: > 0 } host)
        {
            return $"the private DNS server {host} is not reached through the tunnel";
        }

        if (TunnelValidated == false)
        {
            return "the system sees no internet through the tunnel";
        }

        return "the tunnel carries, the failure lies beyond it";
    }

    /// <summary>
    /// The verdict followed by what it was read from.
    /// </summary>
    public string Describe() => $"{Verdict()} ({Details()})";

    /// <summary>
    /// What the moment looked like, without the verdict.
    /// </summary>
    public string Details()
    {
        var parts = new List<string> { $"tunnel {Tunnel}" };
        if (Up)
        {
            parts.Add(HandshakeAgeSeconds >= 0 ? $"handshake {HandshakeAgeSeconds} s ago" : "no handshake yet");
            parts.Add($"receives {RxBitsPerSecond / 1000} kbit/s");
            parts.Add(LinkHealth.LossKnown(LossPercent) ? $"echoes lost {LossPercent}%" : "nothing answers echoes");
            if (Churning)
            {
                parts.Add("session re-established over and over");
            }

            if (TunnelValidated is { } validated)
            {
                parts.Add(validated ? "tunnel validated" : "tunnel not validated");
            }
        }

        if (Under.Length > 0)
        {
            parts.Add(UnderValidated == false ? $"network {Under}, not validated" : $"network {Under}");
        }

        if (PrivateDnsHost is { Length: > 0 } host)
        {
            parts.Add($"private DNS strict {host}");
        }
        else if (PrivateDnsActive is { } active)
        {
            parts.Add(active ? "private DNS automatic, in use" : "private DNS not in use");
        }

        return string.Join(", ", parts);
    }
}
