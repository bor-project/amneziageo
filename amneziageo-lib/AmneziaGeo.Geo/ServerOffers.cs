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

    /// <summary>
    /// How many connects wait for services that never answered before they are asked behind the connect.
    /// </summary>
    public const int SilentWaits = 3;

    /// <summary>
    /// How long the answer of a server of ours stands before it is asked again behind a connect.
    /// </summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromDays(1);

    /// <summary>
    /// How long a server of ours is let be after a question that got no answer of ours; the pause doubles with every
    /// such question in a row, up to <see cref="FreshFor"/>.
    /// </summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many answers of another kind in a row take a server of ours back where no refresh asked.
    /// </summary>
    public const int ForeignAnswers = 3;

    // Two configs of one server answer at once: one of them adds what it hands out, the other finds it held.
    private static readonly SemaphoreSlim _listing = new(1, 1);

    private readonly IStateStore _store;
    private readonly GeoConfigurator? _geo;
    private readonly Action<IReadOnlyList<GeoSource>>? _fetch;
    private readonly Action? _rerouted;
    private readonly Func<IReadOnlyList<long>, CancellationToken, Task>? _take;
    private readonly Func<ServiceTarget, CancellationToken, Task<HelloReply>> _ask;
    private readonly Func<ServiceTarget, CancellationToken, Task<HelloReply>> _askInside;
    private readonly Func<string, bool>? _carried;
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
        Action<IReadOnlyList<GeoSource>>? fetch = null,
        Action? rerouted = null,
        Func<IReadOnlyList<long>, CancellationToken, Task>? take = null,
        Func<string, bool>? carried = null,
        Func<ServiceTarget, CancellationToken, Task<HelloReply>>? askInside = null)
    {
        _store = store;
        _note = note;
        _ask = ask ?? ServerHello.AskAsync;
        _askInside = askInside ?? ServerHello.AskAsync;
        _carried = carried;
        _time = time ?? TimeProvider.System;
        _geo = geo;
        _fetch = fetch;
        _rerouted = rerouted;
        _take = take;
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
        (await AskBindingAsync(config, text, false, false, ct).ConfigureAwait(false)).Offer;

    /// <summary>
    /// Asks the server of a config now and returns the websocket front that holds for its tunnel afterwards: null
    /// where the config is not carried inside a websocket or its server offers no front.
    /// </summary>
    public async Task<WsEndpoint?> FrontAsync(string config, string? text, CancellationToken ct)
    {
        var offer = await AskAsync(config, text, ct).ConfigureAwait(false);
        var transport = await _store.GetConfigTransportAsync(config, ct).ConfigureAwait(false);

        return transport?.UseWebSocket == true ? WsEndpoint.Of(text, offer, transport) : null;
    }

    /// <summary>
    /// Asks the server of a config before its tunnel comes up where it was never asked under the text of the config
    /// or never answered yet; otherwise takes the kept answer at once and asks behind the connect, so the connect does
    /// not wait. A server that kept silent through <see cref="SilentWaits"/> connects is asked behind the connect too.
    /// </summary>
    public async Task<ServerOffer> BeforeConnectAsync(string config, string? text, Func<string, Task>? changed, CancellationToken ct)
    {
        var kept = await ServerOfferStore.KeptAsync(_store, config, text, ct).ConfigureAwait(false);
        if (kept is not null && (kept.Heard || kept.Waits >= SilentWaits))
        {
            Warm([(config, text)], changed);

            return kept.Offer;
        }

        return (await AskBindingAsync(config, text, true, false, ct).ConfigureAwait(false)).Offer;
    }

    /// <summary>
    /// Asks the servers of the configs in the background, one question per config at a time, a server of ours only
    /// while it keeps the subscription off, when its answer is <see cref="FreshFor"/> old or the pause after a
    /// question it did not answer ran out, and names every config whose offer settles the tunnel otherwise now.
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
    /// Asks in the background the servers of the configs that were never asked under their text or never answered,
    /// naming every config whose offer settles the tunnel otherwise now. Returns the configs it asks.
    /// </summary>
    public async Task<IReadOnlyList<string>> WarmUnaskedAsync(IEnumerable<(string Config, string? Text)> targets, Func<string, Task>? changed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var unasked = new List<(string Config, string? Text)>();
        foreach (var (config, text) in targets)
        {
            if (ConfigServices.Target(text) is not null
                && await ServerOfferStore.KeptAsync(_store, config, text, ct).ConfigureAwait(false) is not { Heard: true })
            {
                unasked.Add((config, text));
            }
        }

        Warm(unasked, changed);

        return [.. unasked.Select(target => target.Config)];
    }

    /// <summary>
    /// Asks the servers of the configs at once, as a refresh does, and waits for every answer, naming every config
    /// whose offer settles the tunnel otherwise now; an answer of another kind takes a server of ours back at once.
    /// </summary>
    public async Task AskEachAsync(IEnumerable<(string Config, string? Text)> targets, Func<string, Task>? changed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);

        await Task.WhenAll(targets.Select(target => AskOneAsync(target.Config, target.Text, changed, true, ct))).ConfigureAwait(false);
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

    // Asks the server of a config, keeps what it said and binds the config to the subscription it names. Services
    // that never answered are kept as silent, with the connects that waited for them counted; a question that gets
    // no answer, or an answer of another kind short of a series or a refresh, leaves an answer kept before.
    private async Task<(ServerOffer Offer, bool Bound)> AskBindingAsync(string config, string? text, bool waited, bool refresh, CancellationToken ct)
    {
        if (ConfigServices.Target(text) is not { } point)
        {
            return (ServerOffer.None, false);
        }

        var kept = await ServerOfferStore.KeptAsync(_store, config, text, ct).ConfigureAwait(false);
        var transport = await _store.GetConfigTransportAsync(config, ct).ConfigureAwait(false);
        var asked = point with { TakesSignal = transport?.AllowSignal ?? true };
        var reply = await InsideAsync(config, asked, kept, ct).ConfigureAwait(false) ?? await _ask(asked, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        if (!reply.Heard)
        {
            if (kept is { Heard: true })
            {
                await ServerOfferStore.KeepAsync(_store, config, point, kept with { Misses = kept.Misses + 1, Tried = now }, ct).ConfigureAwait(false);
                if (kept.Misses == 0)
                {
                    _note?.Invoke($"{config}: the services at {point} gave no answer; what they offered stays, and they are asked again after a pause", null);
                }

                return (kept.Offer, false);
            }

            var waits = (kept?.Waits ?? 0) + (waited ? 1 : 0);
            await ServerOfferStore.WriteAsync(_store, config, point, ServerOffer.None, _time.GetUtcNow(), false, waits, ct).ConfigureAwait(false);
            if (kept is null)
            {
                _note?.Invoke($"{config}: the services at {point} did not answer; they are asked again before the next connects", null);
            }

            return (ServerOffer.None, false);
        }

        var offer = reply.Offer ?? ServerOffer.None;
        if (!offer.Ours && kept is { Offer.Ours: true } && !refresh && kept.Foreign + 1 < ForeignAnswers)
        {
            await ServerOfferStore.KeepAsync(_store, config, point, kept with { Misses = kept.Misses + 1, Foreign = kept.Foreign + 1, Tried = now }, ct)
                .ConfigureAwait(false);
            if (kept.Foreign == 0)
            {
                _note?.Invoke($"{config}: the services at {point} answered as a server of another kind; it stays a server of ours until {ForeignAnswers} such answers in a row", null);
            }

            return (kept.Offer, false);
        }

        await ServerOfferStore.WriteAsync(_store, config, point, offer, now, ct).ConfigureAwait(false);
        if (offer.Ours != (kept?.Offer.Ours ?? false))
        {
            _note?.Invoke(offer.Ours
                ? $"{config}: the server at {point} offers {string.Join(", ", offer.Features.Keys)}"
                : $"{config}: no server of ours answers at {point}", null);
        }

        var bound = await BindAsync(config, text, offer, ct).ConfigureAwait(false);
        var listed = await ListAsync(config, offer, ct).ConfigureAwait(false);
        var limited = offer.PresetsOnly != (kept?.Offer.PresetsOnly ?? false);
        if (limited)
        {
            _note?.Invoke(offer.PresetsOnly
                ? $"{config}: the server allows none but its own routing lists"
                : $"{config}: the server allows any routing list again", null);
        }

        return (offer, bound || listed || limited);
    }

    // Asks the server inside the tunnel, plain, while the tunnel of the config carries and the server named where it
    // answers there; null when no server of ours answered there, so the question goes past the tunnel.
    private async Task<HelloReply?> InsideAsync(string config, ServiceTarget point, KeptOffer? kept, CancellationToken ct)
    {
        if (kept?.Offer.Inside() is not { Length: > 0 } origin || _carried?.Invoke(config) != true)
        {
            return null;
        }

        var reply = await _askInside(point with { Origin = origin }, ct).ConfigureAwait(false);

        return reply.Offer is { Ours: true } ? reply : null;
    }

    // Takes the geo sources and the routing lists the server hands out, has the files of the sources that came or
    // moved fetched and the newer versions of the lists the server keeps read-only taken. Without the geo of the
    // device it leaves all of it as it is.
    private async Task<bool> ListAsync(string config, ServerOffer offer, CancellationToken ct)
    {
        if (_geo is null)
        {
            return false;
        }

        var (changed, taking) = await KeepListsAsync(config, offer, _geo, ct).ConfigureAwait(false);
        if (taking.Count == 0 || _take is not { } take)
        {
            return changed;
        }

        try
        {
            await take(taking, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _note?.Invoke($"{config}: the newer versions of the read-only lists of the server were not taken; they are taken at the next answer", ex);
        }

        return changed;
    }

    // Keeps what the server hands out under the lock two configs of one server share, and returns whether anything
    // changed and which read-only lists are to take their newer version.
    private async Task<(bool Changed, IReadOnlyList<long> Taking)> KeepListsAsync(string config, ServerOffer offer, GeoConfigurator geo, CancellationToken ct)
    {
        var sources = offer.Sources();
        var presets = offer.Presets();
        await _listing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var taken = await OfferedLists.TakeSourcesAsync(_store, sources, ct).ConfigureAwait(false);
            if (taken.Added.Count > 0)
            {
                _note?.Invoke($"{config}: the server added the geo sources {string.Join(", ", taken.Added.Select(source => source.Name))}", null);
            }

            if (taken.Moved.Count > 0)
            {
                _note?.Invoke($"{config}: the geo sources {string.Join(", ", taken.Moved.Select(source => source.Name))} took the addresses the server names", null);
            }

            if (taken.Any)
            {
                _fetch?.Invoke(taken.Fetch);
            }

            var lists = await OfferedLists.TakePresetsAsync(_store, geo, config, presets, ct).ConfigureAwait(false);
            if (lists.Any)
            {
                var use = lists.InUse is null ? string.Empty : "; a list the server marks default was put in use";
                var kept = lists.TakeNow.Count == 0 ? string.Empty : $", {lists.TakeNow.Count} read-only taking the newer version";
                var marks = lists.Marked == 0 ? string.Empty : $", {lists.Marked} made read-only or ordinary again";
                _note?.Invoke($"{config}: of the routing lists of the server {lists.Added} added, {lists.Waiting} with a newer version{kept}{marks}{use}", null);
            }

            if (lists.InUse is not null)
            {
                _rerouted?.Invoke();
            }

            return (taken.Any || lists.Any, lists.TakeNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _note?.Invoke($"{config}: the geo sources and routing lists the server hands out were not kept", ex);

            return (false, []);
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

    // Whether a server of ours is asked again: while it keeps the subscription off, a day after its answer, or after a
    // pause that doubles with every question in a row that got no answer of ours.
    private bool Due(KeptOffer kept)
    {
        if (kept.Offer.Subscription() is { Fault.Length: > 0 })
        {
            return true;
        }

        var now = _time.GetUtcNow();
        if (kept.Misses == 0)
        {
            return now - kept.Asked >= FreshFor;
        }

        var pause = TimeSpan.FromTicks(Math.Min(FreshFor.Ticks, RetryAfter.Ticks << Math.Min(kept.Misses - 1, 16)));

        return now - kept.Tried >= pause;
    }

    // Asks one server in the background and lets the next question to it through.
    private async Task WarmOneAsync(string config, string? text, Func<string, Task>? changed)
    {
        try
        {
            await AskOneAsync(config, text, changed, false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _asking.Remove(config);
            }
        }
    }

    // Asks one server and names the config when what it settles or the subscription it names changed; a server of
    // ours is let be behind the connects while its answer stands.
    private async Task AskOneAsync(string config, string? text, Func<string, Task>? changed, bool refresh, CancellationToken ct)
    {
        try
        {
            if (!refresh && await ServerOfferStore.KeptAsync(_store, config, text, ct).ConfigureAwait(false) is { Offer.Ours: true } kept && !Due(kept))
            {
                return;
            }

            var before = await OfferAsync(config, text, ct).ConfigureAwait(false);
            var (after, bound) = await AskBindingAsync(config, text, false, refresh, ct).ConfigureAwait(false);
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
