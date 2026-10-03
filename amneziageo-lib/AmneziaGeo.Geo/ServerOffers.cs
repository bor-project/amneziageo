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

    // Two configs of one server answer at once: one of them adds what it hands out, the other finds it held.
    private static readonly SemaphoreSlim _listing = new(1, 1);

    private readonly IStateStore _store;
    private readonly GeoConfigurator? _geo;
    private readonly Action<IReadOnlyList<GeoSource>>? _fetch;
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
        TimeProvider? time = null,
        GeoConfigurator? geo = null,
        Action<IReadOnlyList<GeoSource>>? fetch = null)
    {
        _store = store;
        _note = note;
        _ask = ask ?? ServerHello.AskAsync;
        _time = time ?? TimeProvider.System;
        _geo = geo;
        _fetch = fetch;
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
    public async Task<ServerOffer> AskAsync(string config, string? text, CancellationToken ct) =>
        (await AskBindingAsync(config, text, ct).ConfigureAwait(false)).Offer;

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
    /// Asks in the background the servers of the configs that were never asked under their text, naming every config
    /// whose offer settles the tunnel otherwise now. Returns the configs it asks.
    /// </summary>
    public async Task<IReadOnlyList<string>> WarmUnaskedAsync(IEnumerable<(string Config, string? Text)> targets, Func<string, Task>? changed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var unasked = new List<(string Config, string? Text)>();
        foreach (var (config, text) in targets)
        {
            if (ConfigServices.Target(text) is not null
                && await ServerOfferStore.KeptAsync(_store, config, text, ct).ConfigureAwait(false) is null)
            {
                unasked.Add((config, text));
            }
        }

        Warm(unasked, changed);

        return [.. unasked.Select(target => target.Config)];
    }

    /// <summary>
    /// Asks the servers of the configs at once and waits for every answer, naming every config whose offer settles
    /// the tunnel otherwise now.
    /// </summary>
    public async Task AskEachAsync(IEnumerable<(string Config, string? Text)> targets, Func<string, Task>? changed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);

        await Task.WhenAll(targets.Select(target => AskOneAsync(target.Config, target.Text, changed, ct))).ConfigureAwait(false);
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

    // Asks the server of a config, keeps what it said and binds the config to the subscription it names.
    private async Task<(ServerOffer Offer, bool Bound)> AskBindingAsync(string config, string? text, CancellationToken ct)
    {
        if (ConfigServices.Target(text) is not { } point)
        {
            return (ServerOffer.None, false);
        }

        var kept = await ServerOfferStore.KeptAsync(_store, config, text, ct).ConfigureAwait(false);
        var reply = await _ask(point, ct).ConfigureAwait(false);
        if (!reply.Heard && kept is not null)
        {
            return (kept.Offer, false);
        }

        var offer = reply.Offer ?? ServerOffer.None;
        await ServerOfferStore.WriteAsync(_store, config, point, offer, _time.GetUtcNow(), ct).ConfigureAwait(false);
        if (offer.Ours != (kept?.Offer.Ours ?? false))
        {
            _note?.Invoke(offer.Ours
                ? $"{config}: the server at {point} offers {string.Join(", ", offer.Features.Keys)}"
                : $"{config}: no server of ours answers at {point}", null);
        }

        var bound = await BindAsync(config, text, offer, ct).ConfigureAwait(false);
        var listed = await ListAsync(config, offer, ct).ConfigureAwait(false);

        return (offer, bound || listed);
    }

    // Adds the geo sources and the routing lists the server hands out that the store holds none of, and has the files
    // of the new sources fetched. Without the geo of the device it leaves both as they are.
    private async Task<bool> ListAsync(string config, ServerOffer offer, CancellationToken ct)
    {
        var sources = offer.Sources();
        var presets = offer.Presets();
        if (_geo is null || (sources.Count == 0 && presets.Count == 0))
        {
            return false;
        }

        await _listing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var added = await OfferedLists.AddSourcesAsync(_store, sources, ct).ConfigureAwait(false);
            if (added.Count > 0)
            {
                _note?.Invoke($"{config}: the server added the geo sources {string.Join(", ", added.Select(source => source.Name))}", null);
                _fetch?.Invoke(added);
            }

            var lists = await OfferedLists.AddPresetsAsync(_store, _geo, presets, ct).ConfigureAwait(false);
            if (lists > 0)
            {
                _note?.Invoke($"{config}: the server added {lists} routing list(s)", null);
            }

            return added.Count > 0 || lists > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _note?.Invoke($"{config}: the geo sources and routing lists the server hands out were not kept", ex);

            return false;
        }
        finally
        {
            _listing.Release();
        }
    }

    // Binds the config to the subscription its server names.
    private async Task<bool> BindAsync(string config, string? text, ServerOffer offer, CancellationToken ct)
    {
        if (offer.Subscription() is not { } offered)
        {
            return false;
        }

        try
        {
            return await SubscriptionBinding.BindAsync(_store, config, text, offered, _time.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _note?.Invoke($"{config}: the subscription the server names was not kept", ex);

            return false;
        }
    }

    // Asks one server in the background and lets the next question to it through.
    private async Task WarmOneAsync(string config, string? text, Func<string, Task>? changed)
    {
        try
        {
            await AskOneAsync(config, text, changed, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _asking.Remove(config);
            }
        }
    }

    // Asks one server and names the config when what it settles or the subscription it names changed.
    private async Task AskOneAsync(string config, string? text, Func<string, Task>? changed, CancellationToken ct)
    {
        try
        {
            var before = await OfferAsync(config, text, ct).ConfigureAwait(false);
            var (after, bound) = await AskBindingAsync(config, text, ct).ConfigureAwait(false);
            if (changed is not null && (bound || !before.Settles(after)))
            {
                await changed(config).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _note?.Invoke($"{config}: the server could not be asked what it offers", ex);
        }
    }
}
