using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Ipc;

/// <summary>
/// What the agent knows of the way out when the tunnel stops carrying: whether the device holds a network, its
/// gateway, the server outside the tunnel with a TCP port to knock at, the addresses of the bases of rules and updates
/// the application reaches anyway, and the way past the tunnel the platform gives.
/// </summary>
public sealed record LinkCauseOptions(
    bool? Network,
    string? Gateway,
    IPAddress? Server,
    int ServerPort,
    IReadOnlyList<IPEndPoint> Bases,
    int HandshakeAgeSeconds,
    Func<Socket, bool>? Bypass = null,
    int TimeoutMs = 3_000);

/// <summary>
/// Where a tunnel that stopped carrying is broken.
/// </summary>
/// <param name="Cause">One of the causes <see cref="LinkCause"/> names.</param>
/// <param name="Text">What was found, in English, for the journal and the diagnostics archive.</param>
public sealed record LinkCauseReport(string Cause, string Text);

/// <summary>
/// Tells where a tunnel that stopped carrying is broken: the device has no way out, the internet answers and the
/// server does not, the server answers and the tunnel carries nothing, or none of these could be told.
/// </summary>
public static class LinkCause
{
    /// <summary>
    /// The device holds no network, or nothing past its gateway answers.
    /// </summary>
    public const string NoNetwork = "no-network";

    /// <summary>
    /// The internet answers outside the tunnel, the server does not.
    /// </summary>
    public const string ServerSilent = "server-silent";

    /// <summary>
    /// The server answers outside the tunnel, and the tunnel carries nothing.
    /// </summary>
    public const string TunnelStuck = "tunnel-stuck";

    /// <summary>
    /// What was found does not tell the place.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// How many of the bases are knocked at, at most.
    /// </summary>
    public const int BasesAsked = 4;

    /// <summary>
    /// The base the agents ask for new versions (<see cref="AmneziaGeo.Decl.UpdateFeed.ReleasesUrl"/>).
    /// </summary>
    public const string UpdateBase = "https://api.github.com/";

    /// <summary>
    /// Knocks at the gateway, the server and the bases outside the tunnel and names the place the link is broken.
    /// </summary>
    public static async Task<LinkCauseReport> FindAsync(LinkCauseOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Network == false)
        {
            return Decide(options, null, null, null);
        }

        var gateway = options.Gateway is { Length: > 0 } address
            ? (await ChannelProbe.EchoLegAsync(CheckLegs.Gateway, address, null, false, ct).ConfigureAwait(false)).State is LegState.Ok or LegState.Weak
            : (bool?)null;
        var server = options.Server is { } host ? await ServerAnswersAsync(host, options, ct).ConfigureAwait(false) : (bool?)null;
        var bases = options.Bases.Where(one => options.Server is null || !one.Address.Equals(options.Server)).Take(BasesAsked).ToList();
        var answered = bases.Count == 0 ? null : await FirstAnswerAsync(bases, options, ct).ConfigureAwait(false);

        return Decide(options, gateway, server, answered);
    }

    /// <summary>
    /// Names the place from what was found: whether the gateway and the server answered (null where they were not
    /// asked) and which base answered first.
    /// </summary>
    public static LinkCauseReport Decide(LinkCauseOptions options, bool? gateway, bool? server, IPEndPoint? answered)
    {
        ArgumentNullException.ThrowIfNull(options);

        var basesKnown = options.Bases.Any(one => options.Server is null || !one.Address.Equals(options.Server));
        if (options.Network == false)
        {
            return new LinkCauseReport(NoNetwork, "the device holds no network");
        }

        if (server is null)
        {
            return new LinkCauseReport(Unknown, "the address of the server is not known");
        }

        var at = $"{options.Server}";
        if (server == true)
        {
            return options.HandshakeAgeSeconds is >= 0 and < LinkRecovery.DefaultDeadHandshakeSeconds
                ? new LinkCauseReport(TunnelStuck, $"the server {at} answers outside the tunnel, its last handshake was {options.HandshakeAgeSeconds} s ago, but nothing comes back through the tunnel")
                : new LinkCauseReport(TunnelStuck, $"the server {at} answers outside the tunnel, but the tunnel gets no answer to its handshake");
        }

        if (answered is { } reached)
        {
            return new LinkCauseReport(ServerSilent, $"the internet answers ({reached}), the server {at} does not, outside the tunnel either");
        }

        if (!basesKnown)
        {
            return new LinkCauseReport(Unknown, $"the server {at} does not answer outside the tunnel, and no address of the bases of rules and updates is known to tell the internet from it");
        }

        return gateway switch
        {
            true => new LinkCauseReport(NoNetwork, $"the gateway answers, but neither the server {at} nor the bases of rules and updates do: the network goes no further than the gateway"),
            false => new LinkCauseReport(NoNetwork, $"neither the gateway, nor the server {at}, nor the bases of rules and updates answer: the device has no working network"),
            _ => new LinkCauseReport(NoNetwork, $"neither the server {at} nor the bases of rules and updates answer outside the tunnel: the device has no way out to the internet"),
        };
    }

    /// <summary>
    /// The addresses of the hosts the given addresses of bases name, as they resolve now; a name that does not resolve
    /// is left out.
    /// </summary>
    public static async Task<IReadOnlyList<IPEndPoint>> ResolveAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(urls);

        var found = new List<IPEndPoint>();
        var hosts = urls
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null)
            .OfType<Uri>()
            .GroupBy(uri => (uri.Host, uri.Port))
            .Select(group => group.First());
        foreach (var uri in hosts)
        {
            try
            {
                var addresses = IPAddress.TryParse(uri.Host, out var literal)
                    ? new[] { literal }
                    : await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
                if ((addresses.FirstOrDefault(one => one.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()) is { } address)
                {
                    found.Add(new IPEndPoint(address, uri.Port));
                }
            }
            catch (SocketException)
            {
            }
        }

        return found;
    }

    // The server answers a knock at its port, a refusal included, or else an echo.
    private static async Task<bool> ServerAnswersAsync(IPAddress server, LinkCauseOptions options, CancellationToken ct)
    {
        if (options.ServerPort > 0 && await KnockAsync(new IPEndPoint(server, options.ServerPort), true, options, ct).ConfigureAwait(false))
        {
            return true;
        }

        var echo = await ChannelProbe.EchoLegAsync(CheckLegs.Endpoint, server.ToString(), options.Bypass, false, ct).ConfigureAwait(false);

        return echo.State is LegState.Ok or LegState.Weak;
    }

    // The first of the bases that takes a connection, knocked at all at once.
    private static async Task<IPEndPoint?> FirstAnswerAsync(IReadOnlyList<IPEndPoint> bases, LinkCauseOptions options, CancellationToken ct)
    {
        var knocks = bases.Select(async one => await KnockAsync(one, false, options, ct).ConfigureAwait(false) ? one : null).ToList();
        while (knocks.Count > 0)
        {
            var done = await Task.WhenAny(knocks).ConfigureAwait(false);
            knocks.Remove(done);
            if (await done.ConfigureAwait(false) is { } answered)
            {
                return answered;
            }
        }

        return null;
    }

    // Connects past the tunnel; a refused connection counts where the host answering is all that is asked.
    private static async Task<bool> KnockAsync(IPEndPoint target, bool refusedAnswers, LinkCauseOptions options, CancellationToken ct)
    {
        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (options.Bypass is { } bypass && !bypass(socket))
        {
            return false;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(options.TimeoutMs);
        try
        {
            await socket.ConnectAsync(target, limit.Token).ConfigureAwait(false);

            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return refusedAnswers;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
