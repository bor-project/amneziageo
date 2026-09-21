using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

/// <summary>
/// Asks the services of the servers of the configs what they offer and keeps the answers in the store.
/// </summary>
public sealed class ServerOffers
{
    /// <summary>
    /// How long a pass must still stand for a speed run to take it.
    /// </summary>
    public static readonly TimeSpan PassMargin = TimeSpan.FromMinutes(1);

    private readonly IStateStore _store;
    private readonly Func<ServiceTarget, CancellationToken, Task<HelloReply>> _ask;
    private readonly Action<string, Exception?>? _note;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _asking = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// ctor
    /// </summary>
    public ServerOffers(
        IStateStore store,
        Action<string, Exception?>? note = null,
        Func<ServiceTarget, CancellationToken, Task<HelloReply>>? ask = null,
        TimeProvider? time = null)
    {
        _store = store;
        _note = note;
        _ask = ask ?? ServerHello.AskAsync;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns what the server of a config offers, as last kept.
    /// </summary>
    public Task<ServerOffer> OfferAsync(string config, string? text, CancellationToken ct) =>
        ServerOfferStore.ReadAsync(_store, config, text, ct);

    /// <summary>
    /// Asks the server of a config now and keeps what it said. Services that answered nothing leave the kept offer
    /// as it stands. Returns the offer that holds afterwards.
    /// </summary>
    public async Task<ServerOffer> AskAsync(string config, string? text, CancellationToken ct)
    {
        if (ConfigServices.Target(text) is not { } point)
        {
            return ServerOffer.None;
        }

        var kept = await ServerOfferStore.KeptAsync(_store, config, text, ct).ConfigureAwait(false);
        var reply = await _ask(point, ct).ConfigureAwait(false);
        if (!reply.Heard && kept is not null)
        {
            return kept.Offer;
        }

        var offer = reply.Offer ?? ServerOffer.None;
        await ServerOfferStore.WriteAsync(_store, config, point, offer, _time.GetUtcNow(), ct).ConfigureAwait(false);
        if (offer.Ours != (kept?.Offer.Ours ?? false))
        {
            _note?.Invoke(offer.Ours
                ? $"{config}: the server at {point} offers {string.Join(", ", offer.Features.Keys)}"
                : $"{config}: no server of ours answers at {point}", null);
        }

        return offer;
    }

    /// <summary>
    /// Asks the server of a config before its tunnel comes up: at once where it was ours or never asked, in the
    /// background where it offered nothing, so a server of another kind does not hold the connect back.
    /// </summary>
    public async Task<ServerOffer> BeforeConnectAsync(string config, string? text, Func<string, Task>? changed, CancellationToken ct)
    {
        var kept = await ServerOfferStore.KeptAsync(_store, config, text, ct).ConfigureAwait(false);
        if (kept is { Offer.Ours: false })
        {
            Warm([(config, text)], changed);

            return kept.Offer;
        }

        return await AskAsync(config, text, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the servers of the configs in the background, one question per config at a time, and names every config
    /// whose offer settles the tunnel otherwise now.
    /// </summary>
    public void Warm(IEnumerable<(string Config, string? Text)> targets, Func<string, Task>? changed = null)
    {
        ArgumentNullException.ThrowIfNull(targets);

        foreach (var (config, text) in targets)
        {
            lock (_lock)
            {
                if (!_asking.Add(config))
                {
                    continue;
                }
            }

            _ = Task.Run(() => WarmOneAsync(config, text, changed));
        }
    }

    /// <summary>
    /// Returns an offer whose pass for a speed run still stands, or null when the server does not measure.
    /// </summary>
    public async Task<ServerOffer?> SpeedAsync(string config, string? text, CancellationToken ct)
    {
        var offer = await OfferAsync(config, text, ct).ConfigureAwait(false);
        if (offer.Speed(true) is not null && offer.SpeedExpires - _time.GetUtcNow() > PassMargin)
        {
            return offer;
        }

        var asked = await AskAsync(config, text, ct).ConfigureAwait(false);

        return asked.Speed(true) is not null && asked.SpeedExpires - _time.GetUtcNow() > PassMargin ? asked : null;
    }

    /// <summary>
    /// Returns the address a probe uploads to and whether it is the server of the config: the one chosen, else the
    /// server inside the tunnel, or beside it for a probe held past the tunnel.
    /// </summary>
    public static (string Url, bool Own) Upload(string chosen, ServerOffer? offer, string path)
    {
        ArgumentNullException.ThrowIfNull(chosen);

        if (chosen.Length > 0)
        {
            return (chosen, false);
        }

        return offer?.Speed(path != ProbePaths.Bypass) is { } leg ? (leg.Up, true) : (string.Empty, false);
    }

    /// <summary>
    /// Returns the address a throughput leg pulls bytes from inside the tunnel or beside it, empty where the server
    /// of the config measures nothing.
    /// </summary>
    public static string Download(ServerOffer? offer, bool inside) => offer?.Speed(inside)?.Down ?? string.Empty;

    // Asks one server and names the config when what it settles changed.
    private async Task WarmOneAsync(string config, string? text, Func<string, Task>? changed)
    {
        try
        {
            var before = await OfferAsync(config, text, CancellationToken.None).ConfigureAwait(false);
            var after = await AskAsync(config, text, CancellationToken.None).ConfigureAwait(false);
            if (changed is not null && !before.Settles(after))
            {
                await changed(config).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _note?.Invoke($"{config}: the server could not be asked what it offers", ex);
        }
        finally
        {
            lock (_lock)
            {
                _asking.Remove(config);
            }
        }
    }
}
