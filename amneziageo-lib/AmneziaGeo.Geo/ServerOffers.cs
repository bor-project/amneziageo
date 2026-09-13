using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

/// <summary>
/// One configuration to ask about: its name, the text it dials with, whether its tunnel is up, and the API port of
/// its settings, zero for the port of the Endpoint.
/// </summary>
public sealed record OfferTarget(string Config, string Text, bool Connected, int ApiPort = 0);

/// <summary>
/// Keeps what the servers of the configurations offer and asks again only after a tunnel comes up or a text changes.
/// </summary>
public sealed class ServerOffers
{
    /// <summary>
    /// The one the agents ask through.
    /// </summary>
    public static ServerOffers Shared { get; } = new();

    private readonly Dictionary<string, Known> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _up = new(StringComparer.Ordinal);
    private readonly HashSet<string> _asking = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly TimeSpan _retry;

    /// <summary>
    /// ctor
    /// </summary>
    public ServerOffers(TimeSpan? retry = null)
    {
        _retry = retry ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Returns what the server of a configuration offers, from memory.
    /// </summary>
    public ServerOffer Offer(string config)
    {
        lock (_lock)
        {
            return _known.TryGetValue(config, out var kept) && kept.Offer is { } offer ? offer : ServerOffer.None;
        }
    }

    /// <summary>
    /// Notes whether the tunnel of a configuration is up and tells whether it has just come up.
    /// </summary>
    public bool Observe(string config, bool connected)
    {
        lock (_lock)
        {
            return Note(config, connected);
        }
    }

    /// <summary>
    /// Asks the servers of the configurations that changed since they were last asked, in the background.
    /// </summary>
    public void Warm(IEnumerable<OfferTarget> targets)
    {
        foreach (var target in Wanted(targets))
        {
            _ = Task.Run(() => AskAsync(target, CancellationToken.None));
        }
    }

    /// <summary>
    /// Asks the servers of the configurations that changed since they were last asked and waits for the answers.
    /// </summary>
    public async Task WarmAsync(IEnumerable<OfferTarget> targets, CancellationToken ct)
    {
        foreach (var target in Wanted(targets))
        {
            await AskAsync(target, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns a fresh offer with a pass for one speed run, or null when the server does not measure.
    /// </summary>
    public async Task<ServerOffer?> SpeedAsync(OfferTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);

        var kept = default(Known);
        lock (_lock)
        {
            Note(target.Config, target.Connected);
            kept = _known.GetValueOrDefault(target.Config);
            if (kept is not null && Stale(kept, target))
            {
                kept = null;
            }
        }

        if (kept is null || (kept.Offer is { Inside: true } && !target.Connected))
        {
            var asked = await AskAsync(target, ct).ConfigureAwait(false);

            return SpeedArgs.Of(asked) is null ? null : asked;
        }

        if (SpeedArgs.Of(kept.Offer) is null || Keys(target.Text) is not { } keys)
        {
            return null;
        }

        var reply = await ServerHello.AskAsync(kept.Offer!.Origin, keys.Private, keys.Server, kept.Offer.Inside, ct)
            .ConfigureAwait(false);
        if (reply.Offer is null)
        {
            return null;
        }

        var fresh = reply.Offer with { Config = target.Config };
        lock (_lock)
        {
            if (ReferenceEquals(_known.GetValueOrDefault(target.Config), kept))
            {
                _known[target.Config] = kept with { Offer = fresh };
            }
        }

        return SpeedArgs.Of(fresh) is null ? null : fresh;
    }

    /// <summary>
    /// Forgets what a configuration was offered.
    /// </summary>
    public void Forget(string config)
    {
        lock (_lock)
        {
            _known.Remove(config);
        }
    }

    /// <summary>
    /// Returns the address a probe uploads to and whether it is the server of the configuration.
    /// </summary>
    public static (string Url, bool Own) Upload(string chosen, ServerOffer? offer, string path)
    {
        if (chosen.Length > 0)
        {
            return (chosen, false);
        }

        var speed = SpeedArgs.Of(offer);

        return offer is null || speed is null || (offer.Inside && path == ProbePaths.Bypass)
            ? (string.Empty, false)
            : (speed.Up, true);
    }

    // Selects the targets to ask and marks them as being asked.
    private List<OfferTarget> Wanted(IEnumerable<OfferTarget> targets)
    {
        var wanted = new List<OfferTarget>();
        lock (_lock)
        {
            foreach (var target in targets ?? [])
            {
                Note(target.Config, target.Connected);
                var kept = _known.GetValueOrDefault(target.Config);
                if (kept is not null && !Stale(kept, target))
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

    // Records the state of a tunnel and counts a session when it comes up.
    private bool Note(string config, bool connected)
    {
        if (connected && _up.Add(config))
        {
            _sessions[config] = _sessions.GetValueOrDefault(config) + 1;

            return true;
        }

        if (!connected)
        {
            _up.Remove(config);
        }

        return false;
    }

    // Tells whether a kept answer predates the text or the session of a target.
    private bool Stale(Known kept, OfferTarget target) =>
        !string.Equals(kept.Text, Hash(target), StringComparison.Ordinal)
        || (target.Connected && kept.Session != _sessions.GetValueOrDefault(target.Config));

    // Asks one server, once more after a pause when an up tunnel heard nothing, and keeps the answer.
    private async Task<ServerOffer?> AskAsync(OfferTarget target, CancellationToken ct)
    {
        var session = -1;
        lock (_lock)
        {
            _asking.Add(target.Config);
            if (target.Connected)
            {
                session = _sessions.GetValueOrDefault(target.Config);
            }
        }

        try
        {
            var reply = await ReplyAsync(target, ct).ConfigureAwait(false);
            if (reply.Offer is null && !reply.Heard && target.Connected)
            {
                await Task.Delay(_retry, ct).ConfigureAwait(false);
                reply = await ReplyAsync(target, ct).ConfigureAwait(false);
            }

            var offer = reply.Offer is null ? null : reply.Offer with { Config = target.Config };
            lock (_lock)
            {
                _known[target.Config] = new Known(session, Hash(target), offer);
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

    // Returns the answer of the first address of the configuration that is a server of ours.
    private async Task<HelloReply> ReplyAsync(OfferTarget target, CancellationToken ct)
    {
        if (Keys(target.Text) is not { } keys)
        {
            return new HelloReply(null, false);
        }

        var heard = false;
        foreach (var (origin, inside) in Origins(target))
        {
            var reply = await ServerHello.AskAsync(origin, keys.Private, keys.Server, inside, ct).ConfigureAwait(false);
            if (reply.Offer is not null)
            {
                return reply;
            }

            heard |= reply.Heard;
        }

        return new HelloReply(null, heard);
    }

    // Reads the keys the challenge is answered with.
    private static (string Private, string Server)? Keys(string text)
    {
        var privateKey = WgConfigEditor.GetPrivateKey(text) ?? string.Empty;
        var serverKey = WgConfigEditor.GetPeerPublicKey(text) ?? string.Empty;

        return privateKey.Length > 0 && serverKey.Length > 0 ? (privateKey, serverKey) : null;
    }

    // Lists the far ends of an up tunnel at the API port.
    private static IReadOnlyList<(string Origin, bool Inside)> Origins(OfferTarget target)
    {
        var port = Port(target);
        if (!target.Connected || port == 0)
        {
            return [];
        }

        var wanted = new List<(string Origin, bool Inside)>();
        foreach (var peer in LinkLossProbe.PeerTargets(WgConfigEditor.GetAddresses(target.Text)))
        {
            var host = peer.Contains(':', StringComparison.Ordinal) ? $"[{peer}]" : peer;
            wanted.Add((string.Create(CultureInfo.InvariantCulture, $"http://{host}:{port}"), true));
        }

        return wanted;
    }

    // Returns the API port of the settings, else the port of the Endpoint, else zero.
    private static int Port(OfferTarget target)
    {
        if (target.ApiPort is >= 1 and <= 65535)
        {
            return target.ApiPort;
        }

        var endpoint = WgConfigEditor.GetEndpoint(target.Text) ?? string.Empty;
        var colon = endpoint.LastIndexOf(':');

        return colon > 0 && int.TryParse(endpoint[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 1 and <= 65535
                ? port
                : 0;
    }

    private static string Hash(OfferTarget target) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Port(target).ToString(CultureInfo.InvariantCulture) + "\n" + target.Text)));

    // What a server answered, the session it was asked in and the text it was asked with.
    private sealed record Known(int Session, string Text, ServerOffer? Offer);
}
