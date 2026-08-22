using System.Globalization;
using System.Net;
using System.Text;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Path a probe is measured over.
/// </summary>
public static class ProbePaths
{
    /// <summary>
    /// The routing in force decides, exactly as it would for the traffic itself.
    /// </summary>
    public const string Auto = "auto";

    /// <summary>
    /// The target is held in the tunnel for the run.
    /// </summary>
    public const string Tunnel = "tunnel";

    /// <summary>
    /// The target is held past the tunnel for the run.
    /// </summary>
    public const string Bypass = "bypass";
}

/// <summary>
/// Legs of a target probe.
/// </summary>
public static class ProbeLegs
{
    /// <summary>
    /// Whether the target answers at all, and how fast.
    /// </summary>
    public const string Reach = "reach";

    /// <summary>
    /// What the target delivers.
    /// </summary>
    public const string Receive = "receive";

    /// <summary>
    /// What the path accepts, measured against a speed service: an arbitrary target owes nobody an upload.
    /// </summary>
    public const string Send = "send";
}

/// <summary>
/// Verdict keys a probe names its outcome by.
/// </summary>
public static class ProbeVerdicts
{
    /// <summary>
    /// The target never answered. Args: target.
    /// </summary>
    public const string Unreachable = "Probe_Unreachable";

    /// <summary>
    /// The target answers but hands over too little to time a rate. Args: target.
    /// </summary>
    public const string NoRate = "Probe_NoRate";

    /// <summary>
    /// Both rates measured. Args: receive Mbit/s, send Mbit/s, the service the send leg was measured against.
    /// </summary>
    public const string Measured = "Probe_Measured";

    /// <summary>
    /// The path asked for cannot be forced on this system. Args: path.
    /// </summary>
    public const string PathUnavailable = "Probe_PathUnavailable";

    /// <summary>
    /// The path asked for needs a tunnel and none runs.
    /// </summary>
    public const string NotConnected = "Probe_NotConnected";
}

/// <summary>
/// A finished probe of one destination: what each leg measured, over which path, and the phrase that sums it up.
/// </summary>
public sealed record ProbeReport(
    long UnixMs,
    string Target,
    string Path,
    string Taken,
    IReadOnlyList<CheckLeg> Legs,
    string VerdictKey,
    IReadOnlyList<string> VerdictArgs)
{
    /// <summary>
    /// Renders the probe as the ack payload: the header row, one row per leg, then the verdict.
    /// </summary>
    public string ToPayload()
    {
        var rows = new List<string> { $"probe\t{Target}\t{Path}\t{Taken}" };
        foreach (var leg in Legs)
        {
            rows.Add(leg.ToRow());
        }

        var verdict = new StringBuilder("verdict\t").Append(Target).Append('\t').Append(VerdictKey);
        foreach (var arg in VerdictArgs)
        {
            verdict.Append('\t').Append(arg);
        }

        rows.Add(verdict.ToString());
        return string.Join('\n', rows);
    }

    /// <summary>
    /// Reads a probe back from the ack payload.
    /// </summary>
    public static ProbeReport Parse(string payload, long unixMs = 0)
    {
        var legs = new List<CheckLeg>();
        var key = ProbeVerdicts.Unreachable;
        var args = new List<string>();
        var target = string.Empty;
        var path = ProbePaths.Auto;
        var taken = string.Empty;
        foreach (var row in payload.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (CheckLeg.TryParse(row) is { } leg)
            {
                legs.Add(leg);
                continue;
            }

            var parts = row.Split('\t');
            if (parts.Length >= 3 && parts[0] == "probe")
            {
                target = parts[1];
                path = parts[2];
                taken = parts.Length > 3 ? parts[3] : string.Empty;
                continue;
            }

            if (parts.Length >= 3 && parts[0] == "verdict")
            {
                target = target.Length > 0 ? target : parts[1];
                key = parts[2];
                args = [.. parts.Skip(3)];
            }
        }

        return new ProbeReport(unixMs, target, path, taken, legs, key, args);
    }

    /// <summary>
    /// Renders the probe in English for the probe journal and the support archive.
    /// </summary>
    public string Render()
    {
        var text = new StringBuilder();
        var stamp = UnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(UnixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "-";
        text.Append("probe ").Append(Target).Append(" over ").Append(Path);
        if (Taken.Length > 0 && !string.Equals(Taken, Path, StringComparison.Ordinal))
        {
            text.Append(" -> ").Append(Taken);
        }

        text.Append(" at ").Append(stamp).Append('\n');
        foreach (var leg in Legs)
        {
            text.Append("  ").Append(leg.Name.PadRight(10)).Append(leg.State.PadRight(9)).Append(leg.Describe()).Append('\n');
        }

        text.Append("  verdict   ").Append(ProbePhrase.English(VerdictKey, VerdictArgs)).Append('\n');
        return text.ToString();
    }
}

/// <summary>
/// Renders a probe verdict in English for the journal and the support archive.
/// </summary>
public static class ProbePhrase
{
    /// <summary>
    /// Renders a verdict key with its arguments.
    /// </summary>
    public static string English(string key, IReadOnlyList<string> args)
    {
        return key switch
        {
            ProbeVerdicts.Measured => args.Count > 2
                ? $"{Arg(args, 0)} Mbit/s in, {Arg(args, 1)} Mbit/s out against {Arg(args, 2)}"
                : $"{Arg(args, 0)} Mbit/s in, {Arg(args, 1)} Mbit/s out",
            ProbeVerdicts.NoRate => $"{Arg(args, 0)} answers but hands over too little to time a rate",
            ProbeVerdicts.PathUnavailable => $"this system cannot hold traffic {Arg(args, 0)} the tunnel, so nothing was measured",
            ProbeVerdicts.NotConnected => "no tunnel runs, so this path could not be measured",
            _ => $"{Arg(args, 0)} never answered",
        };
    }

    private static string Arg(IReadOnlyList<string> args, int index)
    {
        return index < args.Count ? args[index] : "?";
    }
}

/// <summary>
/// What the probe field offers under it: everything the agent can put a name to, names before bare addresses.
/// </summary>
public static class KnownHostList
{
    /// <summary>
    /// Rows of the probe journal the destinations measured before are read from.
    /// </summary>
    public const int HistoryRows = 200;

    /// <summary>
    /// Renders the list as the ack payload: names first, then addresses, each sorted and offered once.
    /// </summary>
    public static string Payload(IEnumerable<string> hosts)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts)
        {
            var trimmed = host.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (IPAddress.TryParse(trimmed, out _))
            {
                addresses.Add(trimmed);
                continue;
            }

            names.Add(trimmed);
        }

        return string.Join('\n', names.Concat(addresses));
    }
}
