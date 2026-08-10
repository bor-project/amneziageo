using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace AmneziaGeo.Ipc;

/// <summary>
/// One server of a sweep: what it is called, where the tunnel dials it, and whether it is the one running.
/// A carrier port set means the tunnel rides a websocket, and that front is reached by a connect, not an echo.
/// </summary>
public sealed record SweepServer(string Name, string? Address, int CarrierPort = 0, bool Live = false);

/// <summary>
/// What the agent knows before the sweep: the gateway measured once for the whole run, and whether a probe can
/// leave beside a tunnel that is up.
/// </summary>
public sealed record SweepOptions(
    string? Gateway = null,
    bool Connected = false,
    bool TunnelIsDefault = false,
    Func<Socket, bool>? Bypass = null);

/// <summary>
/// One measured server.
/// </summary>
public sealed record SweepRow(
    string Config,
    string State,
    int RttMs = -1,
    int JitterMs = -1,
    int LossPercent = LinkHealth.LossUnknown,
    bool Live = false,
    bool Best = false,
    string Note = "")
{
    /// <summary>
    /// Renders the server as one protocol row.
    /// </summary>
    public string ToRow()
    {
        var row = new StringBuilder("srv\t").Append(Config).Append('\t').Append(State);
        Pair(row, "rtt", RttMs);
        Pair(row, "jitter", JitterMs);
        Pair(row, "loss", LossPercent);
        if (Live)
        {
            row.Append("\tlive=1");
        }

        if (Best)
        {
            row.Append("\tbest=1");
        }

        if (Note.Length > 0)
        {
            row.Append("\tnote=").Append(Note);
        }

        return row.ToString();
    }

    /// <summary>
    /// Reads a server back from its protocol row.
    /// </summary>
    public static SweepRow? TryParse(string row)
    {
        var parts = row.Split('\t');
        if (parts.Length < 3 || parts[0] != "srv")
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

        return new SweepRow(
            parts[1],
            parts[2],
            Number(values, "rtt"),
            Number(values, "jitter"),
            Number(values, "loss"),
            values.ContainsKey("live"),
            values.ContainsKey("best"),
            values.GetValueOrDefault("note", string.Empty));
    }

    /// <summary>
    /// Renders what the server answered, in English, for the log and the support archive.
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

        if (Note.Length > 0)
        {
            parts.Add(Note);
        }

        return parts.Count == 0 ? "-" : string.Join(", ", parts);
    }

    private static void Pair(StringBuilder row, string key, int value)
    {
        if (value >= 0)
        {
            row.Append('\t').Append(key).Append('=').Append(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static int Number(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : -1;
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A finished sweep: the gateway all the servers are measured through, every server as it answered, and the one
/// phrase that names the best of them.
/// </summary>
public sealed record SweepReport(
    long UnixMs,
    IReadOnlyList<SweepRow> Servers,
    CheckLeg? Gateway,
    string VerdictKey,
    IReadOnlyList<string> VerdictArgs,
    string Best)
{
    /// <summary>
    /// Renders the sweep as the ack payload: the gateway leg, one row per server, then the verdict.
    /// </summary>
    public string ToPayload()
    {
        var rows = new List<string>();
        if (Gateway is { } gateway)
        {
            rows.Add(gateway.ToRow());
        }

        foreach (var server in Servers)
        {
            rows.Add(server.ToRow());
        }

        var verdict = new StringBuilder("verdict\t").Append(Best).Append('\t').Append(VerdictKey);
        foreach (var arg in VerdictArgs)
        {
            verdict.Append('\t').Append(arg);
        }

        rows.Add(verdict.ToString());
        return string.Join('\n', rows);
    }

    /// <summary>
    /// Reads a sweep back from the ack payload.
    /// </summary>
    public static SweepReport Parse(string payload, long unixMs = 0)
    {
        var servers = new List<SweepRow>();
        var gateway = default(CheckLeg);
        var key = CheckVerdicts.SweepSilent;
        var args = new List<string>();
        var best = string.Empty;
        foreach (var row in payload.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (SweepRow.TryParse(row) is { } server)
            {
                servers.Add(server);
                continue;
            }

            if (CheckLeg.TryParse(row) is { } leg)
            {
                gateway = leg;
                continue;
            }

            var parts = row.Split('\t');
            if (parts.Length >= 3 && parts[0] == "verdict")
            {
                best = parts[1];
                key = parts[2];
                args = [.. parts.Skip(3)];
            }
        }

        return new SweepReport(unixMs, servers, gateway, key, args, best);
    }

    /// <summary>
    /// Renders the sweep in English for the agent log and the support archive.
    /// </summary>
    public string Render()
    {
        var text = new StringBuilder();
        var stamp = UnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(UnixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "-";
        text.Append("server sweep at ").Append(stamp).Append('\n');
        if (Gateway is { } gateway)
        {
            text.Append("    ").Append(gateway.Name.PadRight(20)).Append(gateway.State.PadRight(9)).Append(gateway.Describe()).Append('\n');
        }

        foreach (var server in Servers)
        {
            text.Append(server.Best ? "  * " : "    ")
                .Append(server.Config.PadRight(20))
                .Append(server.State.PadRight(9))
                .Append(server.Describe())
                .Append(server.Live ? ", running now" : string.Empty)
                .Append('\n');
        }

        text.Append("    verdict   ").Append(CheckPhrase.English(VerdictKey, VerdictArgs)).Append('\n');
        return text.ToString();
    }
}

/// <summary>
/// Measures every saved server with the legs that cost a burst of echoes each. What a server carries is not
/// asked here: a download rides one tunnel at a time, so it belongs to a run of its own.
/// </summary>
public static class ServerSweep
{
    /// <summary>
    /// Measures the servers one after another and returns the finished sweep. They are not measured side by
    /// side on purpose: probes sharing one uplink read each other's queueing as loss.
    /// </summary>
    public static async Task<SweepReport> RunAsync(IReadOnlyList<SweepServer> servers, SweepOptions options, CancellationToken ct)
    {
        var gateway = options.Gateway is { Length: > 0 }
            ? await ChannelProbe.EchoLegAsync(CheckLegs.Gateway, options.Gateway, options.Bypass, false, ct).ConfigureAwait(false)
            : null;

        var measured = new List<SweepRow>();
        foreach (var server in servers)
        {
            measured.Add(await MeasureAsync(server, options, ct).ConfigureAwait(false));
        }

        var (key, args, best) = SweepVerdict.Decide(measured, gateway, InTunnel(options));
        var rows = measured
            .Select(row => row with { Best = best.Length > 0 && string.Equals(row.Config, best, StringComparison.Ordinal) })
            .ToList();

        return new SweepReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rows, gateway, key, args, best);
    }

    /// <summary>
    /// Whether the run had to send through the tunnel it is comparing servers against.
    /// </summary>
    public static bool InTunnel(SweepOptions options)
    {
        return options.Connected && options.TunnelIsDefault && options.Bypass is null;
    }

    // One server: a websocket carrier answers TCP, a plain tunnel answers an echo and nothing else.
    private static async Task<SweepRow> MeasureAsync(SweepServer server, SweepOptions options, CancellationToken ct)
    {
        var leg = server.CarrierPort > 0
            ? await ChannelProbe.ConnectLegAsync(server.Name, server.Address, server.CarrierPort, options.Bypass, ct).ConfigureAwait(false)
            : await ChannelProbe.EchoLegAsync(server.Name, server.Address, options.Bypass, false, ct).ConfigureAwait(false);

        return new SweepRow(server.Name, leg.State, leg.RttMs, leg.JitterMs, leg.LossPercent, server.Live, false, leg.Note);
    }
}

/// <summary>
/// Turns the measured servers into the one phrase the sweep exists for: which server to be on right now.
/// </summary>
public static class SweepVerdict
{
    /// <summary>
    /// Decides the sweep: returns the verdict key, its arguments and the best server.
    /// </summary>
    public static (string Key, IReadOnlyList<string> Args, string Best) Decide(
        IReadOnlyList<SweepRow> servers,
        CheckLeg? gateway,
        bool inTunnel)
    {
        if (servers.Count == 0)
        {
            return (CheckVerdicts.SweepEmpty, [], string.Empty);
        }

        var best = servers
            .Where(row => row.RttMs >= 0 && LinkHealth.LossKnown(row.LossPercent))
            .OrderBy(row => row.LossPercent)
            .ThenBy(row => row.RttMs)
            .FirstOrDefault();

        if (best is null)
        {
            return (CheckVerdicts.SweepSilent, [], string.Empty);
        }

        var answer = Answer(best);
        if (gateway is not null && LinkHealth.LossKnown(gateway.LossPercent) && LinkHealth.Lossy(gateway.LossPercent))
        {
            return (CheckVerdicts.LocalLoss, [Text(gateway.LossPercent)], best.Config);
        }

        // A tunnel the probes had to ride carries them to every server but its own: those numbers compare paths
        // behind one server, not the servers themselves.
        if (inTunnel)
        {
            return (CheckVerdicts.SweepInTunnel, answer, best.Config);
        }

        var live = servers.FirstOrDefault(row => row.Live);
        if (live is not null && !string.Equals(live.Config, best.Config, StringComparison.Ordinal) && Behind(live, best))
        {
            return (CheckVerdicts.SweepSwitch, [live.Config, .. answer], best.Config);
        }

        return (CheckVerdicts.SweepBest, answer, best.Config);
    }

    // Whether the server that is up is worth leaving: it answers nothing, or it loses enough more to be felt.
    private static bool Behind(SweepRow live, SweepRow best)
    {
        if (live.RttMs < 0 || !LinkHealth.LossKnown(live.LossPercent))
        {
            return true;
        }

        return live.LossPercent - best.LossPercent >= LinkHealth.LossyPercent;
    }

    private static IReadOnlyList<string> Answer(SweepRow best)
    {
        return [best.Config, Text(best.RttMs), Text(best.LossPercent)];
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
