using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Ipc;

/// <summary>
/// What one probe measures: the destination, the path it is held on for the run, what the routing made of it,
/// and where the send leg uploads to. The bypass is supplied by the platform that owns the tunnel; without one
/// no socket can be excused from it.
/// </summary>
public sealed record TargetProbeOptions(
    string Target,
    string Path,
    string Taken = "",
    string UploadUrl = "",
    Func<Socket, bool>? Bypass = null);

/// <summary>
/// Measures one destination: whether it answers, what it delivers, and what the same path accepts. The path is
/// held by the caller - routes on the desktops, a protected socket on the phone - so this only measures.
/// </summary>
public static class TargetProbe
{
    // Ports tried when a destination refuses echo: the two a public host almost always listens on.
    private static readonly int[] _ports = [443, 80];

    /// <summary>
    /// Runs the probe and names the outcome.
    /// </summary>
    public static async Task<ProbeReport> RunAsync(TargetProbeOptions options, CancellationToken ct)
    {
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bypass = options.Path == ProbePaths.Bypass ? options.Bypass : null;
        var legs = new List<CheckLeg>();

        var address = await ResolveAsync(options.Target, ct).ConfigureAwait(false);
        if (address is null)
        {
            legs.Add(new CheckLeg(ProbeLegs.Reach, LegState.Unknown, Note: "the name does not resolve"));
            return new ProbeReport(stamp, options.Target, options.Path, options.Taken, legs,
                ProbeVerdicts.Unreachable, [options.Target]);
        }

        var reach = await ReachAsync(address, bypass, ct).ConfigureAwait(false);
        legs.Add(reach);
        if (reach.State is LegState.Unknown or LegState.Bad)
        {
            return new ProbeReport(stamp, options.Target, options.Path, options.Taken, legs,
                ProbeVerdicts.Unreachable, [options.Target]);
        }

        var (receive, bytes) = await ChannelProbe.DownloadAsync(ProbeLegs.Receive, PageUrl(options.Target), bypass, ct)
            .ConfigureAwait(false);
        legs.Add(bytes < ChannelProbe.SourceFloorBytes
            ? receive with { State = LegState.Unknown, BitsPerSecond = -1, Note = "handed over nothing to time" }
            : receive);

        var upload = options.UploadUrl.Length > 0 ? options.UploadUrl : ChannelProbe.DefaultUploadUrl;
        var send = await ChannelProbe.UploadAsync(ProbeLegs.Send, upload, bypass, ct).ConfigureAwait(false);
        legs.Add(send);

        return bytes < ChannelProbe.SourceFloorBytes
            ? new ProbeReport(stamp, options.Target, options.Path, options.Taken, legs, ProbeVerdicts.NoRate, [options.Target])
            : new ProbeReport(stamp, options.Target, options.Path, options.Taken, legs, ProbeVerdicts.Measured,
                [Mbits(receive.BitsPerSecond), Mbits(send.BitsPerSecond)]);
    }

    /// <summary>
    /// Names a probe that never ran because the path asked for is not available here.
    /// </summary>
    public static ProbeReport Refused(string target, string path, string key)
    {
        return new ProbeReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), target, path, string.Empty, [],
            key, [path == ProbePaths.Bypass ? "past" : "through"]);
    }

    // The address behind a target: an address is taken as it stands, a name is resolved.
    private static async Task<IPAddress?> ResolveAsync(string target, CancellationToken ct)
    {
        if (IPAddress.TryParse(target, out var parsed))
        {
            return parsed;
        }

        try
        {
            var found = await Dns.GetHostAddressesAsync(target, ct).ConfigureAwait(false);
            return found.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? found.FirstOrDefault();
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
        {
            return null;
        }
    }

    // Whether the destination answers: an echo first, then a connect, because plenty of hosts drop ICMP.
    private static async Task<CheckLeg> ReachAsync(IPAddress address, Func<Socket, bool>? bypass, CancellationToken ct)
    {
        var echo = await ChannelProbe.EchoLegAsync(ProbeLegs.Reach, address.ToString(), bypass, measureSize: false, ct)
            .ConfigureAwait(false);
        if (echo.State is not LegState.Unknown)
        {
            return echo;
        }

        foreach (var port in _ports)
        {
            var leg = await ChannelProbe.ConnectLegAsync(ProbeLegs.Reach, address.ToString(), port, bypass, ct)
                .ConfigureAwait(false);
            if (leg.State is not LegState.Unknown and not LegState.Bad)
            {
                return leg with { Note = $"{address} answers on {port}" };
            }
        }

        return echo;
    }

    // What a bare target is pulled from: a name or an address becomes the page at its root.
    private static string PageUrl(string target)
    {
        return target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? target
                : $"https://{target}/";
    }

    private static string Mbits(long bits)
    {
        return bits <= 0 ? "0" : (bits / 1_000_000d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
