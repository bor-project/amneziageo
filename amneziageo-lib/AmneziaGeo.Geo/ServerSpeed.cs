using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

/// <summary>
/// One configuration to ask about: its name, the text it dials with, and whether its tunnel is up.
/// </summary>
public sealed record SpeedTarget(string Config, string Text, bool Connected);

/// <summary>
/// What the servers of the configurations answered about measuring the speed. The asking happens in the
/// background and what came back is kept, so the window is answered out of memory and never waits on a server.
/// A pass is short-lived and is taken per run, at the address that was found here.
/// </summary>
public sealed class ServerSpeed
{
    /// <summary>
    /// The one the agents ask through.
    /// </summary>
    public static ServerSpeed Shared { get; } = new();

    // How long an answer stands before the server is asked again.
    private static readonly TimeSpan _life = TimeSpan.FromMinutes(10);

    // How long a server that answered nothing is left alone.
    private static readonly TimeSpan _silence = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, Known> _known = new(StringComparer.Ordinal);
    private readonly HashSet<string> _asking = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly int _port;

    /// <summary>
    /// ctor
    /// </summary>
    public ServerSpeed(int port = ServerHello.PanelPort)
    {
        _port = port;
    }

    /// <summary>
    /// What the window is told about where a configuration measures: read out of what was asked before, so
    /// nothing here goes to the network.
    /// </summary>
    public SpeedService Told(string config)
    {
        lock (_lock)
        {
            return _known.TryGetValue(config, out var kept) && kept.Origin.Length > 0
                ? new SpeedService(true, config, Authority(kept.Origin))
                : SpeedService.None;
        }
    }

    /// <summary>
    /// Asks the servers of these configurations what they offer, each in the background and each at most once
    /// at a time. Nothing is waited for: what comes back lands in what the window is told next.
    /// </summary>
    public void Warm(IEnumerable<SpeedTarget> targets)
    {
        foreach (var target in Wanted(targets))
        {
            _ = Task.Run(() => AskAsync(target, CancellationToken.None));
        }
    }

    /// <summary>
    /// The same, awaited: the answers are in by the time it returns.
    /// </summary>
    public async Task WarmAsync(IEnumerable<SpeedTarget> targets, CancellationToken ct)
    {
        foreach (var target in Wanted(targets))
        {
            await AskAsync(target, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The addresses one run measures against, with a pass of its own. A server known to offer nothing is not
    /// asked again here, and one never asked is asked now: a run is long enough to carry that.
    /// </summary>
    public async Task<SpeedOffer?> TicketAsync(SpeedTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);

        var known = Kept(target.Config);
        if (known is null || known.Connected != target.Connected)
        {
            return await AskAsync(target, ct).ConfigureAwait(false);
        }

        if (known.Origin.Length == 0)
        {
            return null;
        }

        var keys = Keys(target.Text);
        if (keys is null)
        {
            return null;
        }

        return await ServerHello.AskAsync(known.Origin, keys.Value.Private, keys.Value.Server, known.Inside, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets what a configuration was told, so the next asking starts over.
    /// </summary>
    public void Forget(string config)
    {
        lock (_lock)
        {
            _known.Remove(config);
        }
    }

    /// <summary>
    /// The address a probe uploads to: the one the settings name, else the one the server offers, else nothing,
    /// which leaves the built-in service to decide. A server reached inside the tunnel is no use to a run held
    /// past it.
    /// </summary>
    public static (string Url, bool Own) Upload(string chosen, SpeedOffer? offer, string path)
    {
        if (chosen.Length > 0)
        {
            return (chosen, false);
        }

        return offer is null || (offer.Inside && path == ProbePaths.Bypass)
            ? (string.Empty, false)
            : (offer.Up, true);
    }

    // The targets worth asking about right now: what stands is left alone, and so is what is already being asked.
    private List<SpeedTarget> Wanted(IEnumerable<SpeedTarget> targets)
    {
        var wanted = new List<SpeedTarget>();
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            foreach (var target in targets ?? [])
            {
                var kept = _known.GetValueOrDefault(target.Config);
                if (kept is not null && kept.Connected == target.Connected && kept.Until > now)
                {
                    continue;
                }

                if (_asking.Add(target.Config))
                {
                    wanted.Add(target);
                }
            }
        }

        return wanted;
    }

    // Asks one server and keeps what it answered; the addresses come back for the run that asked.
    private async Task<SpeedOffer?> AskAsync(SpeedTarget target, CancellationToken ct)
    {
        lock (_lock)
        {
            _asking.Add(target.Config);
        }

        try
        {
            var offer = await OfferAsync(target, ct).ConfigureAwait(false);
            var until = DateTimeOffset.UtcNow + (offer is null ? _silence : _life);
            lock (_lock)
            {
                _known[target.Config] = new Known(target.Connected, offer?.Origin ?? string.Empty, offer?.Inside ?? false, until);
            }

            return offer;
        }
        finally
        {
            lock (_lock)
            {
                _asking.Remove(target.Config);
            }
        }
    }

    // The first address of the configuration that answers as a server of ours.
    private async Task<SpeedOffer?> OfferAsync(SpeedTarget target, CancellationToken ct)
    {
        if (Keys(target.Text) is not { } keys)
        {
            return null;
        }

        foreach (var (origin, inside) in Origins(target.Text, target.Connected))
        {
            var offer = await ServerHello.AskAsync(origin, keys.Private, keys.Server, inside, ct).ConfigureAwait(false);
            if (offer is not null)
            {
                return offer;
            }
        }

        return null;
    }

    // The keys the challenge is answered with; a configuration without them asks nothing.
    private static (string Private, string Server)? Keys(string text)
    {
        var privateKey = WgConfigEditor.GetPrivateKey(text) ?? string.Empty;
        var serverKey = WgConfigEditor.GetPeerPublicKey(text) ?? string.Empty;

        return privateKey.Length > 0 && serverKey.Length > 0 ? (privateKey, serverKey) : null;
    }

    // Where the panel is looked for: the far end of the tunnel while one runs, then the endpoint the tunnel dials.
    private IReadOnlyList<(string Origin, bool Inside)> Origins(string text, bool connected)
    {
        var wanted = new List<(string Origin, bool Inside)>();
        if (connected)
        {
            foreach (var peer in LinkLossProbe.PeerTargets(WgConfigEditor.GetAddresses(text)))
            {
                Keep(wanted, peer, true);
            }
        }

        var endpoint = WgConfigEditor.GetEndpoint(text) ?? string.Empty;
        var colon = endpoint.LastIndexOf(':');
        var host = (colon > 0 ? endpoint[..colon] : endpoint).Trim('[', ']').Trim();
        if (host.Length > 0)
        {
            Keep(wanted, host, false);
        }

        return wanted;
    }

    // Both schemes of one host: the panel stands behind TLS by default and plainly where none was set up.
    private void Keep(List<(string Origin, bool Inside)> wanted, string host, bool inside)
    {
        var written = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        wanted.Add(($"https://{written}:{_port}", inside));
        wanted.Add(($"http://{written}:{_port}", inside));
    }

    private Known? Kept(string config)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            var kept = _known.GetValueOrDefault(config);
            return kept is not null && kept.Until > now ? kept : null;
        }
    }

    // The host and port of an origin, as the window shows them.
    private static string Authority(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var parsed) ? parsed.Authority : origin;

    // What a server answered, and until when that stands; an empty address means it offers no measuring.
    private sealed record Known(bool Connected, string Origin, bool Inside, DateTimeOffset Until);
}
