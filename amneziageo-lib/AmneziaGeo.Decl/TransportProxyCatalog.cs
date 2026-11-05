using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AmneziaGeo.Decl;

/// <summary>
/// Reports a transport proxy error.
/// </summary>
public sealed class TransportProxyFault : InvalidOperationException
{
    public string Code { get; }

    /// <summary>
    /// ctor
    /// </summary>
    public TransportProxyFault(string code) : base(code)
    {
        Code = code;
    }
}

/// <summary>
/// Reports a transport-level availability check.
/// </summary>
public sealed record TransportProxyProbeResult(TransportProxyAvailability Availability, string Fault = "", DateTimeOffset? Checked = null);

/// <summary>
/// Maintains the common proxy catalog and explicit selections.
/// </summary>
public sealed class TransportProxyCatalog
{
    private static readonly ConditionalWeakTable<IStateStore, TransportProxyCatalog> Instances = new();
    private readonly IStateStore _store;
    private Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? _configurations;
    private readonly ConcurrentDictionary<ProxyReference, (string Stamp, TransportProxyProbeResult Result)> _probes = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// ctor
    /// </summary>
    public TransportProxyCatalog(IStateStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Returns the catalog of a state store.
    /// </summary>
    public static TransportProxyCatalog For(IStateStore store,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? configurations = null)
    {
        var catalog = Instances.GetValue(store, one => new TransportProxyCatalog(one));
        if (configurations is not null)
        {
            Volatile.Write(ref catalog._configurations, configurations);
        }

        return catalog;
    }

    /// <summary>
    /// Checks whether a configuration remains registered.
    /// </summary>
    public async Task<bool> HasConfigurationAsync(string config, CancellationToken ct = default) =>
        (await ConfigurationsAsync(ct).ConfigureAwait(false)).ContainsKey(config);

    /// <summary>
    /// Applies an authenticated hello publication.
    /// </summary>
    public async Task<bool> SynchronizeAsync(string publisher, ServiceTarget target, ServerOffer offer, int endpointPort = 0, CancellationToken ct = default)
    {
        var configurations = await ConfigurationsAsync(ct).ConfigureAwait(false);
        if (!configurations.TryGetValue(publisher, out var text) || ConfigServices.Target(text)?.Mark() != target.Mark())
        {
            return false;
        }

        var before = (await _store.GetTransportProxyStateAsync(ct).ConfigureAwait(false)).Revision;
        await ChangeAsync(state => Synchronize(state, publisher, target, offer, endpointPort), ct).ConfigureAwait(false);
        return before != (await _store.GetTransportProxyStateAsync(ct).ConfigureAwait(false)).Revision;
    }

    /// <summary>
    /// Migrates the transport choice of one configuration.
    /// </summary>
    public async Task EnsureSelectionAsync(string config, CancellationToken ct = default)
    {
        var configurations = await ConfigurationsAsync(ct).ConfigureAwait(false);
        if (!configurations.TryGetValue(config, out var text))
        {
            throw new TransportProxyFault("unknown-config");
        }

        var held = await _store.GetTransportProxyStateAsync(ct).ConfigureAwait(false);
        var target = ConfigServices.Target(text);
        if (held.Selections.Any(one => one.Config == config)
            && (target is null || held.Publications.Any(one => one.Publisher == config && one.Slot == "catalog" && one.Mark == target.Mark())))
        {
            return;
        }

        var transport = await _store.GetConfigTransportAsync(config, ct).ConfigureAwait(false);
        var kept = await ServerOfferStore.KeptAsync(_store, config, text, ct).ConfigureAwait(false);
        await ChangeAsync(state =>
        {
            var next = Migrate(state, config, text, transport, kept?.Offer).State;
            if (target is not null && kept is { Heard: true, Offer.Ours: true }
                && !next.Publications.Any(one => one.Publisher == config && one.Slot == "catalog" && one.Mark == target.Mark()))
            {
                next = Synchronize(next, config, target, kept.Offer, ConfigServices.EndpointPort(text)).State;
            }

            return (next, true);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the explicit choice of a configuration.
    /// </summary>
    public async Task<TransportProxySelection> SelectionAsync(string config, CancellationToken ct = default)
    {
        await EnsureSelectionAsync(config, ct).ConfigureAwait(false);
        return (await _store.GetTransportProxyStateAsync(ct).ConfigureAwait(false)).Selections.Single(one => one.Config == config);
    }

    /// <summary>
    /// Selects one registered proxy or direct UDP.
    /// </summary>
    public async Task SelectAsync(string config, ProxyReference? reference, CancellationToken ct = default)
    {
        if (!(await ConfigurationsAsync(ct).ConfigureAwait(false)).ContainsKey(config))
        {
            throw new TransportProxyFault("unknown-config");
        }

        var context = await ContextAsync(ct).ConfigureAwait(false);
        await ChangeAsync(state =>
        {
            if (reference is not null && !Candidates(state, context).Any(one => one.Record.Reference == reference))
            {
                throw new TransportProxyFault("proxy-missing");
            }

            var selections = state.Selections.Where(one => one.Config != config).Append(new TransportProxySelection(config, reference)).ToArray();
            return (state with { Selections = selections }, true);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies an explicit legacy transport edit.
    /// </summary>
    public async Task LegacySettingsAsync(string config, ConfigTransport? previous, ConfigTransport updated,
        bool explicitChoice = false, CancellationToken ct = default)
    {
        var configurations = await ConfigurationsAsync(ct).ConfigureAwait(false);
        if (!configurations.TryGetValue(config, out var text))
        {
            throw new TransportProxyFault("unknown-config");
        }

        var offer = (await ServerOfferStore.KeptAsync(_store, config, text, ct).ConfigureAwait(false))?.Offer;
        await ChangeAsync(state =>
        {
            var selection = state.Selections.FirstOrDefault(one => one.Config == config);
            var legacy = selection?.Reference is { } reference
                && state.Locals.Concat(state.Publications.SelectMany(one => one.Records)).Any(one => one.Reference == reference && one.Legacy);
            if (!explicitChoice && (previous?.UseWebSocket ?? false) == updated.UseWebSocket && !legacy)
            {
                return (state, false);
            }

            return Migrate(state with { Selections = [.. state.Selections.Where(one => one.Config != config)] }, config, text, updated, offer);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves a local proxy definition.
    /// </summary>
    public async Task<ProxyReference> SaveLocalAsync(string? id, OfferedProxy draft, CancellationToken ct = default)
    {
        if (draft is not { Name: not null, Host: not null, Path: not null, Tls: not null, Auth: not null })
        {
            throw new TransportProxyFault("bad-proxy");
        }

        var key = id ?? Guid.NewGuid().ToString("N");
        return await ChangeAsync(state =>
        {
            var held = state.Locals.FirstOrDefault(one => one.Reference.Id == key);
            if (id is not null && held is null)
            {
                throw new TransportProxyFault("proxy-not-local");
            }

            var definition = draft with { Id = key, Name = draft.Name.Trim(), Host = draft.Host.Trim().Trim('[', ']'),
                Path = draft.Path.Trim('/'), Updated = DateTimeOffset.UtcNow };
            Check(definition, held?.Legacy == true);
            var reference = new ProxyReference(state.LocalSource, key);
            var record = held is null ? new CatalogTransportProxy(reference, definition, Local: true) : held with { Proxy = definition };
            return (state with { Locals = [.. state.Locals.Where(one => one.Reference != reference), record] }, reference);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a local proxy definition.
    /// </summary>
    public async Task<CatalogTransportProxy?> LocalAsync(ProxyReference reference, CancellationToken ct = default) =>
        (await _store.GetTransportProxyStateAsync(ct).ConfigureAwait(false)).Locals.FirstOrDefault(one => one.Reference == reference);

    /// <summary>
    /// Removes a local proxy without replacing its selections.
    /// </summary>
    public Task<bool> RemoveLocalAsync(ProxyReference reference, CancellationToken ct = default) => ChangeAsync(state =>
    {
        var held = state.Locals.FirstOrDefault(one => one.Reference == reference)
            ?? throw new TransportProxyFault("proxy-not-local");
        return (Retire(state with { Locals = [.. state.Locals.Where(one => one.Reference != reference)] }, [held]), true);
    }, ct);

    /// <summary>
    /// Resolves the selected proxy for the original UDP endpoint.
    /// </summary>
    public async Task<ResolvedTransportProxy?> ResolveAsync(string config, CancellationToken ct = default)
    {
        var selection = await SelectionAsync(config, ct).ConfigureAwait(false);
        if (selection.Reference is not { } reference)
        {
            return null;
        }

        var context = await ContextAsync(ct).ConfigureAwait(false);
        if (!context.TryGetValue(config, out var endpoint) || endpoint.Target is null || endpoint.Port is < 1 or > 65535
            || Uri.CheckHostName(endpoint.Target.Host) == UriHostNameType.Unknown)
        {
            throw new TransportProxyFault("bad-endpoint");
        }

        var state = await _store.GetTransportProxyStateAsync(ct).ConfigureAwait(false);
        var candidate = Choose(Candidates(state, context).Where(one => one.Record.Reference == reference), config)
            ?? throw new TransportProxyFault("proxy-missing");
        var record = candidate.Record;
        Check(record.Proxy, record.Legacy);
        if (record.Legacy && (record.LegacyIssuer != endpoint.Target.ServerKey || record.LegacyPort != endpoint.Port))
        {
            throw new TransportProxyFault("proxy-legacy-target");
        }

        return new ResolvedTransportProxy(record, candidate.Publisher, endpoint.Target.Host, endpoint.Port);
    }

    /// <summary>
    /// Returns public metadata from every registered source.
    /// </summary>
    public async Task<IReadOnlyList<TransportProxySummary>> ListAsync(CancellationToken ct = default)
    {
        foreach (var name in (await ConfigurationsAsync(ct).ConfigureAwait(false)).Keys)
        {
            await EnsureSelectionAsync(name, ct).ConfigureAwait(false);
        }

        var context = await ContextAsync(ct).ConfigureAwait(false);
        var state = await _store.GetTransportProxyStateAsync(ct).ConfigureAwait(false);
        var candidates = Candidates(state, context).ToArray();
        var summaries = new List<TransportProxySummary>();
        foreach (var group in candidates.GroupBy(one => one.Record.Reference))
        {
            var chosen = Choose(group, "")!;
            var record = chosen.Record;
            var definition = record.Proxy;
            if (record.Legacy && !state.Selections.Any(one => one.Reference == record.Reference)
                && group.Any(alias => candidates.Any(one => one.Publisher == alias.Publisher && !one.Record.Legacy
                    && one.Record.Proxy.Transport == "websocket" && one.Record.Proxy.Port == definition.Port
                    && string.Equals(one.Record.Proxy.Host, definition.Host, StringComparison.OrdinalIgnoreCase)
                    && one.Record.Proxy.Path == definition.Path)))
            {
                continue;
            }

            var probe = _probes.TryGetValue(record.Reference, out var remembered) && remembered.Stamp == Stamp(record)
                ? remembered.Result : new TransportProxyProbeResult(TransportProxyAvailability.Unknown);
            summaries.Add(new TransportProxySummary(record.Reference, definition.Name, definition.Transport, definition.Host,
                definition.Port, record.Legacy ? "" : definition.Path, record.Local,
                [.. group.Select(one => one.Origin).Where(origin => origin.Length > 0).Distinct(StringComparer.Ordinal)],
                probe.Availability, probe.Fault.Length > 0 ? probe.Fault : chosen.Error, probe.Checked));
        }

        foreach (var reference in state.Selections.Where(one => context.ContainsKey(one.Config)).Select(one => one.Reference).OfType<ProxyReference>().Distinct()
            .Where(reference => !summaries.Any(one => one.Reference == reference)))
        {
            var retired = state.Retired.LastOrDefault(one => one.Reference == reference);
            summaries.Add(new TransportProxySummary(reference, retired?.Name ?? reference.Id, retired?.Transport ?? "",
                retired?.Host ?? "", retired?.Port ?? 0, retired?.Path ?? "", retired?.Local ?? false, retired?.Sources ?? [],
                TransportProxyAvailability.Unavailable, "proxy-missing", Missing: true));
        }

        return [.. summaries.OrderBy(one => one.Name, StringComparer.OrdinalIgnoreCase).ThenBy(one => one.Reference.Source, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Checks a proxy through the platform transport.
    /// </summary>
    public async Task<TransportProxyProbeResult> ProbeAsync(ProxyReference reference,
        Func<CatalogTransportProxy, CancellationToken, Task<TransportProxyProbeResult>> probe, CancellationToken ct = default)
    {
        var context = await ContextAsync(ct).ConfigureAwait(false);
        var state = await _store.GetTransportProxyStateAsync(ct).ConfigureAwait(false);
        var candidate = Choose(Candidates(state, context).Where(one => one.Record.Reference == reference), "");
        var result = candidate is null ? new TransportProxyProbeResult(TransportProxyAvailability.Unavailable, "proxy-missing")
            : await probe(candidate.Record, ct).ConfigureAwait(false);
        result = result with { Checked = DateTimeOffset.UtcNow };
        _probes[reference] = (candidate is null ? "" : Stamp(candidate.Record), result);
        return result;
    }

    /// <summary>
    /// Follows a configuration rename in its publications and choice.
    /// </summary>
    public Task<bool> RenameAsync(string oldName, string newName, CancellationToken ct = default) => ChangeAsync(state =>
        oldName == newName || !state.Publications.Any(one => one.Publisher == oldName) && !state.Selections.Any(one => one.Config == oldName)
            && !state.Locals.Any(one => one.LegacyPublisher == oldName) ? (state, false) : (state with
        {
            Publications = [.. state.Publications.Where(one => one.Publisher != newName).Select(one => one.Publisher == oldName ? one with
            {
                Publisher = newName,
                Records = [.. one.Records.Select(record => record.LegacyPublisher == oldName ? record with { LegacyPublisher = newName } : record)],
            } : one)],
            Selections = [.. state.Selections.Where(one => one.Config != newName || !state.Selections.Any(other => other.Config == oldName))
                .Select(one => one.Config == oldName ? one with { Config = newName } : one)],
            Locals = [.. state.Locals.Select(one => one.LegacyPublisher == oldName ? one with { LegacyPublisher = newName } : one)],
        }, true), ct);

    /// <summary>
    /// Withdraws the publications and choice of a removed configuration.
    /// </summary>
    public Task<bool> ForgetAsync(string config, CancellationToken ct = default) => ChangeAsync(state =>
    {
        var withdrawn = state.Publications.Where(one => one.Publisher == config).SelectMany(one => one.Records)
            .Concat(state.Locals.Where(one => one.LegacyPublisher == config)).ToArray();
        var sources = state.Publications.Where(one => one.Publisher == config).Select(one => one.Origin).ToArray();
        return (Retire(state with
        {
            Publications = [.. state.Publications.Where(one => one.Publisher != config)],
            Locals = [.. state.Locals.Where(one => one.LegacyPublisher != config)],
            Selections = [.. state.Selections.Where(one => one.Config != config)],
        }, withdrawn, sources), true);
    }, ct);

    private static (TransportProxyState State, bool Changed) Synchronize(TransportProxyState state, string publisher, ServiceTarget target, ServerOffer offer, int endpointPort)
    {
        var legacy = offer.Ours && offer.WebSocketPort > 0
            ? new CatalogTransportProxy(new ProxyReference(ProxySourceIdentity.Legacy(target.ServerKey), "legacy"),
                new OfferedProxy("legacy", LegacyName(publisher), "websocket", target.Host, offer.WebSocketPort, offer.WebSocketPath,
                    new OfferedProxyTls(target.Host, ""), new OfferedProxyAuth("legacy", ""), DateTimeOffset.MinValue),
                Legacy: true, LegacyIssuer: target.ServerKey, LegacyPort: endpointPort > 0 ? endpointPort : target.Port,
                LegacyPublisher: publisher, LegacyOffered: true)
            : null;
        var previousLegacy = state.Publications.FirstOrDefault(one => one.Publisher == publisher && one.Slot == "legacy");
        if (legacy is not null && endpointPort == 0 && previousLegacy?.Records.FirstOrDefault() is { } prior)
        {
            legacy = legacy with { LegacyPort = prior.LegacyPort };
        }

        var next = state;
        if (offer.Ours)
        {
            var reference = new ProxyReference(ProxySourceIdentity.Legacy(target.ServerKey), "legacy");
            var upgraded = next.Locals.Where(one => one.Legacy && one.LegacyPublisher == publisher && one.LegacyIssuer == target.ServerKey).ToArray();
            var references = upgraded.Select(one => one.Reference).ToHashSet();
            next = Retire(next with
            {
                Locals = [.. next.Locals.Where(one => !references.Contains(one.Reference))],
                Selections = [.. next.Selections.Select(one => one.Reference is not null && references.Contains(one.Reference)
                    ? one with { Reference = reference } : one)],
            }, upgraded);
        }

        next = Replace(next, new TransportProxyPublication(publisher, target.Mark(), target.ServerKey,
            legacy?.Reference.Source ?? "", legacy is null ? [] : [legacy], target.Host, Slot: "legacy"));
        var previous = next.Publications.FirstOrDefault(one => one.Publisher == publisher && one.Slot == "catalog");
        if (!offer.Ours || !offer.HasProxyCatalog)
        {
            if (previous is { Source.Length: > 0 } && next.Trust.Any(one => one.Source == previous.Source && one.Kind == "signed"))
            {
                return (Replace(next, previous with { Error = "proxy-source-downgrade" }), true);
            }

            return (Replace(next, new TransportProxyPublication(publisher, target.Mark(), target.ServerKey, "", [], target.Host)), true);
        }

        if (offer.ProxyCatalog is not { } catalog)
        {
            return (Error(next, previous, publisher, target, "proxy-catalog-invalid"), true);
        }

        var source = catalog.Source;
        var identity = "";
        var signed = catalog.Proof is not null;
        if (signed && !ProxySourceIdentity.Verify(catalog, target.ServerKey, out identity))
        {
            return (Error(next, previous, publisher, target, "proxy-source-invalid"), true);
        }

        var trust = next.Trust.ToList();
        if (signed)
        {
            var advertised = source;
            source = ProxySourceIdentity.Signed(source);
            var pinned = trust.FirstOrDefault(one => one.Source == source && one.Kind == "signed");
            if (pinned is not null && pinned.PublicKey != identity)
            {
                return (Error(next, previous, publisher, target, "proxy-source-conflict"), true);
            }

            if (pinned is null)
            {
                trust.Add(new TransportProxyTrust(source, "signed", identity, AdvertisedSource: advertised));
            }

            var unsigned = ProxySourceIdentity.Unsigned(advertised, target.ServerKey);
            if (previous is not null && previous.Source == unsigned && previous.Issuer == target.ServerKey && previous.Mark == target.Mark())
            {
                next = next with { Selections = [.. next.Selections.Select(one => one.Reference?.Source == unsigned
                    ? one with { Reference = one.Reference with { Source = source } } : one)] };
            }
        }
        else
        {
            if (trust.Any(one => one.Source == ProxySourceIdentity.Signed(source) && one.Kind == "signed"))
            {
                return (Error(next, previous, publisher, target, "proxy-source-downgrade"), true);
            }

            source = ProxySourceIdentity.Unsigned(source, target.ServerKey);
            if (!trust.Any(one => one.Source == source))
            {
                trust.Add(new TransportProxyTrust(source, "unsigned", Issuer: target.ServerKey, AdvertisedSource: catalog.Source));
            }
        }

        next = next with { Trust = trust };
        return (Replace(next, new TransportProxyPublication(publisher, target.Mark(), target.ServerKey, source,
            [.. catalog.Items.Select(item => new CatalogTransportProxy(new ProxyReference(source, item.Id), item))], target.Host)), true);
    }

    private static TransportProxyState Error(TransportProxyState state, TransportProxyPublication? previous, string publisher, ServiceTarget target, string error) =>
        Replace(state, previous is null ? new TransportProxyPublication(publisher, target.Mark(), target.ServerKey, "", [], target.Host, error)
            : previous with { Error = error });

    private static TransportProxyState Replace(TransportProxyState state, TransportProxyPublication publication)
    {
        var previous = state.Publications.FirstOrDefault(one => one.Publisher == publication.Publisher && one.Slot == publication.Slot);
        var retired = previous?.Records.Where(record => !publication.Records.Any(one => one.Reference == record.Reference)) ?? [];
        return Retire(state with { Publications = [.. state.Publications.Where(one => one.Publisher != publication.Publisher || one.Slot != publication.Slot), publication] }, retired,
            previous is { Origin.Length: > 0 } ? [previous.Origin] : null);
    }

    private static TransportProxyState Retire(TransportProxyState state, IEnumerable<CatalogTransportProxy> records, IEnumerable<string>? sources = null)
    {
        var retired = state.Retired.ToList();
        foreach (var record in records)
        {
            var remembered = retired.LastOrDefault(one => one.Reference == record.Reference);
            var origins = record.Local ? null : state.Publications.Where(one => one.Records.Any(item => item.Reference == record.Reference))
                .Select(one => one.Origin).Concat(sources ?? []).Concat(remembered?.Sources ?? [])
                .Where(one => one.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(one => one, StringComparer.Ordinal).ToArray();
            retired.RemoveAll(one => one.Reference == record.Reference);
            retired.Add(new RetiredTransportProxy(record.Reference, record.Proxy.Name, record.Proxy.Transport, record.Proxy.Host,
                record.Proxy.Port, record.Legacy ? "" : record.Proxy.Path, record.Local, origins));
        }

        return state with { Retired = retired };
    }

    private static (TransportProxyState State, bool Changed) Migrate(TransportProxyState state, string name, string? text, ConfigTransport? transport, ServerOffer? offer)
    {
        if (state.Selections.Any(one => one.Config == name))
        {
            return (state, false);
        }

        if (transport?.UseWebSocket != true)
        {
            return (state with { Selections = [.. state.Selections, new TransportProxySelection(name, null)] }, true);
        }

        var target = ConfigServices.Target(text);
        var front = WsEndpoint.Of(text, offer, transport);
        if (offer is { Ours: true } && target is not null)
        {
            var reference = new ProxyReference(ProxySourceIdentity.Legacy(target.ServerKey), "legacy");
            var record = front is null ? null : new CatalogTransportProxy(reference,
                Definition("legacy", "WebSocket " + name, front.Value), Legacy: true, LegacyIssuer: target.ServerKey,
                LegacyPort: ConfigServices.EndpointPort(text), LegacyPublisher: name, LegacyOffered: true);
            var next = Replace(state, new TransportProxyPublication(name, target.Mark(), target.ServerKey, reference.Source,
                record is null ? [] : [record], target.Host, Slot: "legacy"));
            return (next with { Selections = [.. next.Selections, new TransportProxySelection(name, reference)] }, true);
        }

        var held = state.Locals.FirstOrDefault(one => one.Legacy && one.LegacyPublisher == name);
        var localReference = held?.Reference ?? new ProxyReference(state.LocalSource, Guid.NewGuid().ToString("N"));
        var local = front is null ? null : new CatalogTransportProxy(localReference,
            Definition(localReference.Id, "WebSocket " + name, front.Value), Local: true, Legacy: true,
            LegacyIssuer: target?.ServerKey ?? "", LegacyPort: ConfigServices.EndpointPort(text), LegacyPublisher: name);
        return (state with
        {
            Locals = local is null ? state.Locals : [.. state.Locals.Where(one => one.Reference != localReference), local],
            Selections = [.. state.Selections, new TransportProxySelection(name, localReference)],
        }, true);
    }

    private static OfferedProxy Definition(string id, string name, WsEndpoint front) => new(id, name.Length > 64 ? name[..64] : name,
        "websocket", front.Host, front.Port, front.PathPrefix.Trim('/'), new OfferedProxyTls(front.Host, ""),
        new OfferedProxyAuth("legacy", front.Credentials), DateTimeOffset.MinValue);

    private static string LegacyName(string publisher)
    {
        var name = "WebSocket " + publisher;
        return name.Length > 64 ? name[..64] : name;
    }

    private static void Check(OfferedProxy proxy, bool legacy)
    {
        if (legacy)
        {
            if (proxy.Transport != "websocket" || !WsEndpoint.Dials(new WsEndpoint(proxy.Host, proxy.Port, proxy.Path, proxy.Auth.Credential).Address())
                || proxy.Port is < 1 or > 65535)
            {
                throw new TransportProxyFault("bad-proxy");
            }

            return;
        }

        var catalog = new OfferedProxyCatalog(1, Guid.NewGuid().ToString("D"), [proxy]);
        var payload = JsonSerializer.Serialize(new { version = 1, source = catalog.Source, items = catalog.Items }, Json);
        using var definition = JsonDocument.Parse(payload);
        if (OfferedProxyCatalog.Read(definition.RootElement) is null)
        {
            throw new TransportProxyFault("bad-proxy");
        }
    }

    private sealed record Context(ServiceTarget? Target, int Port);
    private sealed record Candidate(CatalogTransportProxy Record, string Publisher, string Origin, string Error = "");

    private async Task<IReadOnlyDictionary<string, Context>> ContextAsync(CancellationToken ct)
    {
        var context = new Dictionary<string, Context>(StringComparer.Ordinal);
        foreach (var (name, text) in await ConfigurationsAsync(ct).ConfigureAwait(false))
        {
            context[name] = new Context(ConfigServices.Target(text), ConfigServices.EndpointPort(text));
        }

        return context;
    }

    private async Task<IReadOnlyDictionary<string, string>> ConfigurationsAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _configurations) is { } configurations)
        {
            return await configurations(ct).ConfigureAwait(false);
        }

        var records = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in await _store.ListConfigNamesAsync(ct).ConfigureAwait(false))
        {
            if (await _store.GetConfigTextAsync(name, ct).ConfigureAwait(false) is { } text)
            {
                records[name] = text;
            }
        }

        return records;
    }

    private static IEnumerable<Candidate> Candidates(TransportProxyState state, IReadOnlyDictionary<string, Context> context)
    {
        foreach (var record in state.Locals)
        {
            if (!record.Legacy || context.TryGetValue(record.LegacyPublisher, out var owner)
                && owner.Target?.ServerKey == record.LegacyIssuer && owner.Port == record.LegacyPort)
            {
                yield return new Candidate(record, "", "");
            }
        }

        foreach (var publication in state.Publications)
        {
            if (!context.TryGetValue(publication.Publisher, out var owner) || owner.Target?.Mark() != publication.Mark)
            {
                continue;
            }

            foreach (var record in publication.Records)
            {
                yield return new Candidate(record, publication.Publisher, publication.Origin, publication.Error);
            }
        }
    }

    private static Candidate? Choose(IEnumerable<Candidate> candidates, string config) => candidates
        .OrderByDescending(one => one.Record.Proxy.Updated)
        .ThenBy(one => one.Publisher == config ? 0 : 1)
        .ThenBy(one => one.Publisher, StringComparer.Ordinal).FirstOrDefault();

    private static string Stamp(CatalogTransportProxy record) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Json))));

    private async Task<T> ChangeAsync<T>(Func<TransportProxyState, (TransportProxyState State, T Result)> update, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var held = await _store.GetTransportProxyStateAsync(ct).ConfigureAwait(false);
            var initialized = held.LocalSource.Length == 0 ? held with { LocalSource = Guid.NewGuid().ToString("D") } : held;
            var (next, result) = update(initialized);
            next = Normalize(next);
            if (Normalize(held).ToPayload() == next.ToPayload())
            {
                return result;
            }

            next = next with { Revision = checked(held.Revision + 1) };
            if (await _store.TrySaveTransportProxyStateAsync(next, held.Revision, ct).ConfigureAwait(false))
            {
                return result;
            }

            await Task.Yield();
        }

        throw new TransportProxyFault("proxy-catalog-busy");
    }

    private static TransportProxyState Normalize(TransportProxyState state) => state with
    {
        Locals = [.. state.Locals.OrderBy(one => one.Reference.Source, StringComparer.Ordinal).ThenBy(one => one.Reference.Id, StringComparer.Ordinal)],
        Publications = [.. state.Publications.OrderBy(one => one.Publisher, StringComparer.Ordinal).ThenBy(one => one.Slot, StringComparer.Ordinal)
            .Select(one => one with { Records = [.. one.Records.OrderBy(record => record.Reference.Source, StringComparer.Ordinal).ThenBy(record => record.Reference.Id, StringComparer.Ordinal)] })],
        Trust = [.. state.Trust.OrderBy(one => one.Source, StringComparer.Ordinal).ThenBy(one => one.Kind, StringComparer.Ordinal)],
        Selections = [.. state.Selections.OrderBy(one => one.Config, StringComparer.Ordinal)],
        Retired = [.. state.Retired.OrderBy(one => one.Reference.Source, StringComparer.Ordinal).ThenBy(one => one.Reference.Id, StringComparer.Ordinal)],
    };
}
