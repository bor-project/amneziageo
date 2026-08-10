using System.Globalization;
using System.Text;

namespace AmneziaGeo.Ipc;

/// <summary>
/// How one leg of the ladder came out.
/// </summary>
public static class LegState
{
    /// <summary>
    /// The leg carries what is put into it.
    /// </summary>
    public const string Ok = "ok";

    /// <summary>
    /// The leg works and pays for it: some loss, or a rate far below its neighbours.
    /// </summary>
    public const string Weak = "weak";

    /// <summary>
    /// The leg is where the traffic dies.
    /// </summary>
    public const string Bad = "bad";

    /// <summary>
    /// Nothing on this leg answered, so it says nothing about the link.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The leg needs something this system cannot do.
    /// </summary>
    public const string Skipped = "skipped";
}

/// <summary>
/// Legs of the ladder, near to far. Each one cuts off the layer before it, so the first bad leg names the culprit.
/// </summary>
public static class CheckLegs
{
    /// <summary>
    /// The physical gateway: the local network and the Wi-Fi in front of it.
    /// </summary>
    public const string Gateway = "gateway";

    /// <summary>
    /// The server's address reached outside the tunnel: the path the tunnel is carried over.
    /// </summary>
    public const string Endpoint = "endpoint";

    /// <summary>
    /// The session itself: its age, its counters and how often it is re-established.
    /// </summary>
    public const string Handshake = "handshake";

    /// <summary>
    /// The server's own address inside the tunnel: the tunnel stack on the far side.
    /// </summary>
    public const string Peer = "peer";

    /// <summary>
    /// A download through the tunnel: what the whole chain delivers.
    /// </summary>
    public const string Tunnel = "tunnel";

    /// <summary>
    /// The same download outside the tunnel: what the home channel delivers without the server.
    /// </summary>
    public const string Direct = "direct";
}

/// <summary>
/// Verdict keys both sides name the diagnosis by. The agent renders them in English for the log, the window
/// resolves them from the resources.
/// </summary>
public static class CheckVerdicts
{
    /// <summary>
    /// Nothing runs, so only the legs in front of the tunnel were measured.
    /// </summary>
    public const string NotConnected = "Check_NotConnected";

    /// <summary>
    /// Nothing answered anywhere: the run says nothing about the link.
    /// </summary>
    public const string NoMeasurement = "Check_NoMeasurement";

    /// <summary>
    /// The local network loses packets before they leave the house. Args: loss percent.
    /// </summary>
    public const string LocalLoss = "Check_LocalLoss";

    /// <summary>
    /// The path to the server loses packets. Args: loss percent.
    /// </summary>
    public const string PathLoss = "Check_PathLoss";

    /// <summary>
    /// The path cuts packets above a size. Args: largest passing payload, MTU to set.
    /// </summary>
    public const string PathMtu = "Check_PathMtu";

    /// <summary>
    /// The session is re-established instead of living. Args: rekeys per minute.
    /// </summary>
    public const string Rekeying = "Check_Rekeying";

    /// <summary>
    /// The far side of the tunnel loses packets while the path to it is clean. Args: loss percent.
    /// </summary>
    public const string ServerLoss = "Check_ServerLoss";

    /// <summary>
    /// The tunnel delivers almost nothing while the server answers. Args: bits per second.
    /// </summary>
    public const string ServerSlow = "Check_ServerSlow";

    /// <summary>
    /// The same download is many times faster outside the tunnel. Args: tunnel bits/s, direct bits/s.
    /// </summary>
    public const string TunnelBehindDirect = "Check_TunnelBehindDirect";

    /// <summary>
    /// Nothing to blame. Args: bits per second.
    /// </summary>
    public const string Healthy = "Check_Healthy";

    /// <summary>
    /// Nothing to blame on the legs measured, and the download never rode the tunnel because the list carries
    /// only what it names. Args: bits per second of the path it did ride.
    /// </summary>
    public const string HealthyOutsideTunnel = "Check_HealthyOutsideTunnel";
}

/// <summary>
/// Sizes the tunnel by what the path underneath it carries. An echo measures its own payload, so the packet
/// around it and the headers the tunnel spends on every packet both have to be counted before the number
/// measured becomes an MTU to set.
/// </summary>
public static class TunnelMtu
{
    /// <summary>
    /// IPv4 and ICMP headers wrapped around a probe payload.
    /// </summary>
    public const int EchoHeaders = 28;

    /// <summary>
    /// Outer IPv4, UDP, the WireGuard data header and its tag.
    /// </summary>
    public const int TunnelHeaders = 60;

    /// <summary>
    /// MTU a config that declares none runs at.
    /// </summary>
    public const int Default = 1420;

    /// <summary>
    /// The path MTU a passing payload implies.
    /// </summary>
    public static int PathFor(int payloadBytes)
    {
        return payloadBytes + EchoHeaders;
    }

    /// <summary>
    /// The largest tunnel MTU that payload leaves room for.
    /// </summary>
    public static int PreferredFor(int payloadBytes)
    {
        return payloadBytes + EchoHeaders - TunnelHeaders;
    }
}

/// <summary>
/// The MTU the tunnel runs at against the one the measured path leaves room for. Present only when the
/// configured value does not fit, which is the case that costs packets without saying so.
/// </summary>
public sealed record MtuAdvice(int PayloadBytes, int PathMtu, int ConfiguredMtu, int PreferredMtu)
{
    /// <summary>
    /// The advice a measured payload earns under the MTU in force, or null when the tunnel already fits.
    /// </summary>
    public static MtuAdvice? For(int payloadBytes, int configuredMtu)
    {
        if (payloadBytes <= 0)
        {
            return null;
        }

        var mtu = configuredMtu > 0 ? configuredMtu : TunnelMtu.Default;
        var preferred = TunnelMtu.PreferredFor(payloadBytes);
        return mtu <= preferred
            ? null
            : new MtuAdvice(payloadBytes, TunnelMtu.PathFor(payloadBytes), mtu, preferred);
    }

    /// <summary>
    /// Renders the advice as one protocol row.
    /// </summary>
    public string ToRow()
    {
        return string.Join(
            '\t',
            "advice",
            "mtu",
            Text(PayloadBytes),
            Text(PathMtu),
            Text(ConfiguredMtu),
            Text(PreferredMtu));
    }

    /// <summary>
    /// Reads the advice back from its protocol row.
    /// </summary>
    public static MtuAdvice? TryParse(string row)
    {
        var parts = row.Split('\t');
        if (parts.Length < 6 || parts[0] != "advice" || parts[1] != "mtu")
        {
            return null;
        }

        var numbers = new int[4];
        for (var i = 0; i < numbers.Length; i++)
        {
            if (!int.TryParse(parts[i + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return new MtuAdvice(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    /// <summary>
    /// Renders the advice in English for the agent log and the support archive.
    /// </summary>
    public string Describe()
    {
        return $"the path carries {Text(PayloadBytes)}-byte payloads, {Text(PathMtu)} bytes on the wire, "
            + $"while the tunnel runs at MTU {Text(ConfiguredMtu)}: set it to {Text(PreferredMtu)}";
    }

    /// <summary>
    /// The arguments the localized phrase is rendered with.
    /// </summary>
    public IReadOnlyList<string> Args()
    {
        return [Text(PayloadBytes), Text(ConfiguredMtu), Text(PreferredMtu)];
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One measured leg. A value below zero was not measured.
/// </summary>
public sealed record CheckLeg(
    string Name,
    string State,
    int RttMs = -1,
    int JitterMs = -1,
    int LossPercent = LinkHealth.LossUnknown,
    long BitsPerSecond = -1,
    int MaxPacketBytes = 0,
    int AgeSeconds = -1,
    int RekeysPerMinute = -1,
    long RxBytes = -1,
    long TxBytes = -1,
    string Note = "")
{
    /// <summary>
    /// Renders the leg as one protocol row.
    /// </summary>
    public string ToRow()
    {
        var row = new StringBuilder("leg\t").Append(Name).Append('\t').Append(State);
        Pair(row, "rtt", RttMs);
        Pair(row, "jitter", JitterMs);
        Pair(row, "loss", LossPercent);
        Pair(row, "bps", BitsPerSecond);
        Pair(row, "size", MaxPacketBytes <= 0 ? -1 : MaxPacketBytes);
        Pair(row, "age", AgeSeconds);
        Pair(row, "rekeys", RekeysPerMinute);
        Pair(row, "rx", RxBytes);
        Pair(row, "tx", TxBytes);
        if (Note.Length > 0)
        {
            row.Append("\tnote=").Append(Note);
        }

        return row.ToString();
    }

    /// <summary>
    /// Reads a leg back from its protocol row.
    /// </summary>
    public static CheckLeg? TryParse(string row)
    {
        var parts = row.Split('\t');
        if (parts.Length < 3 || parts[0] != "leg")
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in parts.Skip(3))
        {
            var split = field.IndexOf('=');
            if (split > 0)
            {
                values[field[..split]] = field[(split + 1)..];
            }
        }

        return new CheckLeg(
            parts[1],
            parts[2],
            (int)Number(values, "rtt"),
            (int)Number(values, "jitter"),
            (int)Number(values, "loss"),
            Number(values, "bps"),
            (int)Math.Max(Number(values, "size"), 0),
            (int)Number(values, "age"),
            (int)Number(values, "rekeys"),
            Number(values, "rx"),
            Number(values, "tx"),
            values.GetValueOrDefault("note", string.Empty));
    }

    /// <summary>
    /// Renders what the leg measured, in English, for the log and the support archive.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (RttMs >= 0)
        {
            parts.Add($"rtt {Text(RttMs)} ms");
        }

        if (JitterMs >= 0)
        {
            parts.Add($"jitter {Text(JitterMs)} ms");
        }

        if (LinkHealth.LossKnown(LossPercent))
        {
            parts.Add($"loss {Text(LossPercent)}%");
        }

        if (MaxPacketBytes > 0)
        {
            parts.Add($"{Text(MaxPacketBytes)}-byte packets pass");
        }

        if (AgeSeconds >= 0)
        {
            parts.Add($"age {Text(AgeSeconds)} s");
        }

        if (RekeysPerMinute >= 0)
        {
            parts.Add($"{Text(RekeysPerMinute)} rekey(s) per minute");
        }

        if (BitsPerSecond >= 0)
        {
            parts.Add($"{CheckFormat.Mbits(BitsPerSecond)} Mbit/s");
        }

        if (RxBytes >= 0 && TxBytes >= 0)
        {
            parts.Add($"rx {CheckFormat.Bytes(RxBytes)}, tx {CheckFormat.Bytes(TxBytes)}");
        }

        if (Note.Length > 0)
        {
            parts.Add(Note);
        }

        return parts.Count == 0 ? "-" : string.Join(", ", parts);
    }

    private static void Pair(StringBuilder row, string key, long value)
    {
        if (value >= 0)
        {
            row.Append('\t').Append(key).Append('=').Append(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static long Number(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : -1;
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A finished run: what every leg measured, and the one phrase that names the culprit.
/// </summary>
public sealed record CheckReport(
    long UnixMs,
    string Config,
    IReadOnlyList<CheckLeg> Legs,
    string VerdictKey,
    IReadOnlyList<string> VerdictArgs,
    string Culprit,
    MtuAdvice? Advice = null)
{
    /// <summary>
    /// Renders the run as the ack payload: one row per leg, then the verdict.
    /// </summary>
    public string ToPayload()
    {
        var rows = new List<string>();
        foreach (var leg in Legs)
        {
            rows.Add(leg.ToRow());
        }

        if (Advice is { } advice)
        {
            rows.Add(advice.ToRow());
        }

        var verdict = new StringBuilder("verdict\t").Append(Culprit).Append('\t').Append(VerdictKey);
        foreach (var arg in VerdictArgs)
        {
            verdict.Append('\t').Append(arg);
        }

        rows.Add(verdict.ToString());
        return string.Join('\n', rows);
    }

    /// <summary>
    /// Reads a run back from the ack payload.
    /// </summary>
    public static CheckReport Parse(string payload, long unixMs = 0, string config = "")
    {
        var legs = new List<CheckLeg>();
        var key = CheckVerdicts.NoMeasurement;
        var args = new List<string>();
        var culprit = string.Empty;
        var advice = default(MtuAdvice);
        foreach (var row in payload.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (CheckLeg.TryParse(row) is { } leg)
            {
                legs.Add(leg);
                continue;
            }

            if (MtuAdvice.TryParse(row) is { } parsed)
            {
                advice = parsed;
                continue;
            }

            var parts = row.Split('\t');
            if (parts.Length >= 3 && parts[0] == "verdict")
            {
                culprit = parts[1];
                key = parts[2];
                args = [.. parts.Skip(3)];
            }
        }

        return new CheckReport(unixMs, config, legs, key, args, culprit, advice);
    }

    /// <summary>
    /// Renders the run in English for the agent log and the support archive.
    /// </summary>
    public string Render()
    {
        var text = new StringBuilder();
        var stamp = UnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(UnixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "-";
        text.Append("channel check").Append(Config.Length > 0 ? $" for \"{Config}\"" : string.Empty)
            .Append(" at ").Append(stamp).Append('\n');
        foreach (var leg in Legs)
        {
            text.Append("  ").Append(leg.Name.PadRight(10)).Append(leg.State.PadRight(9)).Append(leg.Describe()).Append('\n');
        }

        if (Advice is { } advice)
        {
            text.Append("  advice    ").Append(advice.Describe()).Append('\n');
        }

        text.Append("  verdict   ").Append(CheckPhrase.English(VerdictKey, VerdictArgs)).Append('\n');
        return text.ToString();
    }
}

/// <summary>
/// Turns a set of measured legs into the one phrase that names the culprit. The order is the ladder's own: the
/// nearest leg that fails owns the verdict, because everything behind it is measured through it.
/// </summary>
public static class ChannelVerdict
{
    /// <summary>
    /// Rate below which a tunnel carries nothing usable, in bits per second.
    /// </summary>
    public const long SlowBitsPerSecond = 1_000_000;

    /// <summary>
    /// How many times faster the direct path must be before the tunnel is called the culprit.
    /// </summary>
    public const int DirectRatio = 5;

    /// <summary>
    /// Payload below which a path counts as cutting packets; a healthy path carries 1472 bytes plus headers.
    /// </summary>
    public const int PathSizeFloor = 1400;

    /// <summary>
    /// Decides the run: returns the verdict key, its arguments and the leg to blame.
    /// </summary>
    public static (string Key, IReadOnlyList<string> Args, string Culprit) Decide(IReadOnlyList<CheckLeg> legs, bool connected)
    {
        var gateway = Leg(legs, CheckLegs.Gateway);
        var endpoint = Leg(legs, CheckLegs.Endpoint);
        var handshake = Leg(legs, CheckLegs.Handshake);
        var peer = Leg(legs, CheckLegs.Peer);
        var tunnel = Leg(legs, CheckLegs.Tunnel);
        var direct = Leg(legs, CheckLegs.Direct);

        if (Lossy(gateway))
        {
            return (CheckVerdicts.LocalLoss, [Text(gateway!.LossPercent)], CheckLegs.Gateway);
        }

        if (Lossy(endpoint))
        {
            return (CheckVerdicts.PathLoss, [Text(endpoint!.LossPercent)], CheckLegs.Endpoint);
        }

        if (endpoint is { MaxPacketBytes: > 0 and < PathSizeFloor })
        {
            return (CheckVerdicts.PathMtu,
                [Text(endpoint.MaxPacketBytes), Text(TunnelMtu.PreferredFor(endpoint.MaxPacketBytes))],
                CheckLegs.Endpoint);
        }

        if (!connected)
        {
            return (CheckVerdicts.NotConnected, [], string.Empty);
        }

        if (handshake is { RekeysPerMinute: >= LinkHealth.ChurnPerMinute })
        {
            return (CheckVerdicts.Rekeying, [Text(handshake.RekeysPerMinute)], CheckLegs.Handshake);
        }

        if (Lossy(peer))
        {
            return (CheckVerdicts.ServerLoss, [Text(peer!.LossPercent)], CheckLegs.Peer);
        }

        if (tunnel is { BitsPerSecond: >= 0 })
        {
            if (direct is { BitsPerSecond: >= 0 } && direct.BitsPerSecond >= tunnel.BitsPerSecond * DirectRatio
                && direct.BitsPerSecond >= SlowBitsPerSecond)
            {
                return (CheckVerdicts.TunnelBehindDirect,
                    [CheckFormat.Mbits(tunnel.BitsPerSecond), CheckFormat.Mbits(direct.BitsPerSecond)], CheckLegs.Tunnel);
            }

            if (tunnel.BitsPerSecond < SlowBitsPerSecond)
            {
                return (CheckVerdicts.ServerSlow, [CheckFormat.Mbits(tunnel.BitsPerSecond)], CheckLegs.Tunnel);
            }

            return (CheckVerdicts.Healthy, [CheckFormat.Mbits(tunnel.BitsPerSecond)], string.Empty);
        }

        if (direct is { BitsPerSecond: >= 0 })
        {
            return (CheckVerdicts.HealthyOutsideTunnel, [CheckFormat.Mbits(direct.BitsPerSecond)], string.Empty);
        }

        var measured = legs.Any(leg => leg.State is LegState.Ok or LegState.Weak or LegState.Bad);
        return measured
            ? (CheckVerdicts.Healthy, [CheckFormat.Mbits(0)], string.Empty)
            : (CheckVerdicts.NoMeasurement, [], string.Empty);
    }

    /// <summary>
    /// The state a measured loss share earns.
    /// </summary>
    public static string StateFor(int lossPercent)
    {
        if (!LinkHealth.LossKnown(lossPercent))
        {
            return LegState.Unknown;
        }

        if (lossPercent >= LinkHealth.LossyPercent * 4)
        {
            return LegState.Bad;
        }

        return LinkHealth.Lossy(lossPercent) ? LegState.Weak : LegState.Ok;
    }

    /// <summary>
    /// The state a measured rate earns.
    /// </summary>
    public static string StateFor(long bitsPerSecond)
    {
        if (bitsPerSecond < 0)
        {
            return LegState.Unknown;
        }

        return bitsPerSecond < SlowBitsPerSecond ? LegState.Bad : LegState.Ok;
    }

    private static CheckLeg? Leg(IReadOnlyList<CheckLeg> legs, string name)
    {
        return legs.FirstOrDefault(leg => leg.Name == name);
    }

    private static bool Lossy(CheckLeg? leg)
    {
        return leg is not null && LinkHealth.LossKnown(leg.LossPercent) && LinkHealth.Lossy(leg.LossPercent);
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The English wording of a verdict, for the log and the support archive; the window has its own translations.
/// </summary>
public static class CheckPhrase
{
    /// <summary>
    /// Renders a verdict key with its arguments.
    /// </summary>
    public static string English(string key, IReadOnlyList<string> args)
    {
        return key switch
        {
            CheckVerdicts.NotConnected => "nothing is connected, so only the legs in front of the tunnel were measured",
            CheckVerdicts.LocalLoss => $"the local network loses {Arg(args, 0)}% of what is sent: the fault is between this device and the router",
            CheckVerdicts.PathLoss => $"the path to the server loses {Arg(args, 0)}%: the fault is between the router and the server",
            CheckVerdicts.PathMtu => $"the path cuts packets above {Arg(args, 0)} bytes: set the tunnel MTU to {Arg(args, 1)}",
            CheckVerdicts.Rekeying => $"the session is re-established {Arg(args, 0)} time(s) a minute: the carrier drops the handshake",
            CheckVerdicts.ServerLoss => $"the far side of the tunnel loses {Arg(args, 0)}% while the path to it is clean: the fault is the server",
            CheckVerdicts.ServerSlow => $"the tunnel delivers {Arg(args, 0)} Mbit/s while the server answers: change the server",
            CheckVerdicts.TunnelBehindDirect => $"the tunnel delivers {Arg(args, 0)} Mbit/s where the same download outside it delivers {Arg(args, 1)}: the fault is the server, not the home channel",
            CheckVerdicts.Healthy => $"nothing to blame: the tunnel delivers {Arg(args, 0)} Mbit/s without loss",
            CheckVerdicts.HealthyOutsideTunnel => $"nothing to blame on the legs measured; the {Arg(args, 0)} Mbit/s belongs to the path beside the tunnel, which is where this download went",
            _ => "nothing answered, so this run says nothing about the link",
        };
    }

    private static string Arg(IReadOnlyList<string> args, int index)
    {
        return index < args.Count ? args[index] : "?";
    }
}

/// <summary>
/// Units both the log and the window print measurements in.
/// </summary>
public static class CheckFormat
{
    /// <summary>
    /// Bits per second as megabits, two decimals at most.
    /// </summary>
    public static string Mbits(long bitsPerSecond)
    {
        return (bitsPerSecond / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A byte count in the largest unit it fills.
    /// </summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double scaled = bytes;
        var unit = 0;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{scaled:0.#} {units[unit]}");
    }
}
