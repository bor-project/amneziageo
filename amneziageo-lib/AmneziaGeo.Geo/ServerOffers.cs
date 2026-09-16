using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

/// <summary>
/// One configuration to ask about: its name, the text it dials with, whether its tunnel is up, the API port of its
/// settings, zero for the port the text names, and the session the tunnel names, empty when the agent counts them.
/// </summary>
public sealed record OfferTarget(string Config, string Text, bool Connected, int ApiPort = 0, string Session = "");

/// <summary>
/// Keeps what the servers of the configurations offer and asks again only after a tunnel comes up or a text changes.
/// </summary>
public sealed class ServerOffers
{
    /// <summary>
    /// The one the agents ask through.
    /// </summary>
    public static ServerOffers Shared { get; } = new();

    /// <summary>
    /// How long a pass must still stand for a run to take it again.
    /// </summary>
    public static readonly TimeSpan PassMargin = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, Known> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _up = new(StringComparer.Ordinal);
    private readonly HashSet<string> _asking = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly TimeSpan _retry;
    private readonly string _file;
    private readonly TimeProvider _time;

    /// <summary>
    /// ctor
    /// </summary>
    public ServerOffers(TimeSpan? retry = null, string file = "", TimeProvider? time = null)
    {
        _retry = retry ?? TimeSpan.FromSeconds(5);
        _file = file;
        _time = time ?? TimeProvider.System;
        Load();
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
    /// Notes whether the tunnel of a configuration is up and tells whether a new session of it has begun.
    /// </summary>
    public bool Observe(string config, bool connected, string session = "")
    {
        lock (_lock)
        {
            return Note(config, connected, session);
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
    /// Returns an offer with a pass that stands for one speed run, or null when the server does not measure.
    /// </summary>
    public async Task<ServerOffer?> SpeedAsync(OfferTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);

        var kept = default(Known);
        lock (_lock)
        {
            Note(target.Config, target.Connected, target.Session);
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

        if (SpeedArgs.Of(kept.Offer) is not { } speed || Keys(target.Text) is not { } keys)
        {
            return null;
        }

        if (speed.Expires - _time.GetUtcNow() > PassMargin)
        {
            return kept.Offer;
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
                Save();
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
            if (_known.Remove(config))
            {
                Save();
            }
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

    /// <summary>
    /// Returns the address a throughput leg pulls bytes from, empty where the server of the configuration measures
    /// nothing or answers inside a tunnel the leg leaves beside.
    /// </summary>
    public static string Download(ServerOffer? offer, bool beside)
    {
        var speed = SpeedArgs.Of(offer);

        return offer is null || speed is null || (offer.Inside && beside) ? string.Empty : speed.Down;
    }

    /// <summary>
    /// Returns the port the server is asked at when the settings name none: the one the text names for the API,
    /// else the port of the Endpoint, else zero.
    /// </summary>
    public static int DefaultPort(string text)
    {
        var points = WgConfigEditor.GetApiPoints(text ?? string.Empty);

        return points.Count > 0 ? points[0].Port : EndpointPort(text ?? string.Empty);
    }

    // Selects the targets to ask and marks them as being asked.
    private List<OfferTarget> Wanted(IEnumerable<OfferTarget> targets)
    {
        var wanted = new List<OfferTarget>();
        lock (_lock)
        {
            foreach (var target in targets ?? [])
            {
                Note(target.Config, target.Connected, target.Session);
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

    // Records the state of a tunnel and starts a session when it comes up or names a session of its own.
    private bool Note(string config, bool connected, string session)
    {
        if (!connected)
        {
            _up.Remove(config);

            return false;
        }

        var came = _up.Add(config);
        if (session.Length > 0)
        {
            if (string.Equals(_sessions.GetValueOrDefault(config), session, StringComparison.Ordinal))
            {
                return false;
            }

            _sessions[config] = session;

            return true;
        }

        if (!came)
        {
            return false;
        }

        _sessions[config] = Guid.NewGuid().ToString("N");

        return true;
    }

    // Tells whether a kept answer predates the text or the session of a target.
    private bool Stale(Known kept, OfferTarget target) =>
        !string.Equals(kept.Text, Hash(target), StringComparison.Ordinal)
        || (target.Connected && !string.Equals(kept.Session, _sessions.GetValueOrDefault(target.Config), StringComparison.Ordinal));

    // Asks one server, once more after a pause when an up tunnel heard nothing, and keeps the answer.
    private async Task<ServerOffer?> AskAsync(OfferTarget target, CancellationToken ct)
    {
        var session = string.Empty;
        lock (_lock)
        {
            _asking.Add(target.Config);
            if (target.Connected)
            {
                session = _sessions.GetValueOrDefault(target.Config) ?? string.Empty;
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
                Save();
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
        foreach (var origin in Origins(target))
        {
            var reply = await ServerHello.AskAsync(origin, keys.Private, keys.Server, true, ct).ConfigureAwait(false);
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

    // Lists the addresses of the server inside an up tunnel: the ones the text names, else the first host of its subnet.
    private static IReadOnlyList<string> Origins(OfferTarget target)
    {
        if (!target.Connected)
        {
            return [];
        }

        var chosen = target.ApiPort is >= 1 and <= 65535 ? target.ApiPort : 0;
        var wanted = new List<string>();
        var named = WgConfigEditor.GetApiPoints(target.Text);
        if (named.Count > 0)
        {
            foreach (var point in named)
            {
                Add(wanted, point.Host, chosen > 0 ? chosen : point.Port);
            }

            return wanted;
        }

        var port = chosen > 0 ? chosen : EndpointPort(target.Text);
        if (port == 0)
        {
            return [];
        }

        foreach (var peer in LinkLossProbe.PeerTargets(WgConfigEditor.GetAddresses(target.Text)))
        {
            Add(wanted, peer, port);
        }

        return wanted;
    }

    // Adds the origin of an address and a port once.
    private static void Add(List<string> origins, string host, int port)
    {
        var bracketed = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        var origin = string.Create(CultureInfo.InvariantCulture, $"http://{bracketed}:{port}");
        if (!origins.Contains(origin, StringComparer.Ordinal))
        {
            origins.Add(origin);
        }
    }

    // Returns the port of the Endpoint, else zero.
    private static int EndpointPort(string text)
    {
        var endpoint = WgConfigEditor.GetEndpoint(text) ?? string.Empty;
        var colon = endpoint.LastIndexOf(':');

        return colon > 0 && int.TryParse(endpoint[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 1 and <= 65535
                ? port
                : 0;
    }

    private static string Hash(OfferTarget target) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ApiPort.ToString(CultureInfo.InvariantCulture) + "\n" + target.Text)));

    // Reads the kept answers back from the file.
    private void Load()
    {
        if (_file.Length == 0 || !File.Exists(_file))
        {
            return;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, Stored>>(File.ReadAllText(_file));
            foreach (var (config, kept) in stored ?? [])
            {
                var offer = kept.Offer is { Length: > 0 } payload ? ServerOffer.Parse(payload) : null;
                _known[config] = new Known(kept.Session ?? string.Empty, kept.Text ?? string.Empty, offer is { Ours: true } ? offer : null);
                if (kept.Session is { Length: > 0 } session)
                {
                    _sessions[config] = session;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _known.Clear();
            _sessions.Clear();
        }
    }

    // Writes the kept answers to the file.
    private void Save()
    {
        if (_file.Length == 0)
        {
            return;
        }

        var stored = _known.ToDictionary(
            pair => pair.Key,
            pair => new Stored(pair.Value.Session, pair.Value.Text, pair.Value.Offer?.ToPayload()),
            StringComparer.Ordinal);
        try
        {
            var temporary = _file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored));
            File.Move(temporary, _file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
    }

    // What a server answered, the session it was asked in and the text it was asked with.
    private sealed record Known(string Session, string Text, ServerOffer? Offer);

    // A kept answer as the file holds it.
    private sealed record Stored(string? Session, string? Text, string? Offer);
}
