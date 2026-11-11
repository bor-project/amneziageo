using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmneziaGeo.Decl;

/// <summary>
/// Carries an explicit transport proxy choice between libraries.
/// </summary>
public sealed record ProxySelectionBlock(ProxyReference? Reference, RetiredTransportProxy? Retired = null,
    TransportProxyTrust? Trust = null, LocalTransportProxyBlock? Local = null);

/// <summary>
/// Carries a selected local proxy definition.
/// </summary>
public sealed record LocalTransportProxyBlock(OfferedProxy Proxy, bool Legacy = false,
    string LegacyIssuer = "", int LegacyPort = 0, bool LegacyOffered = false);

/// <summary>
/// Transfers proxy choices and local definitions between libraries.
/// </summary>
public static class TransportProxyPortability
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Exports the explicit choice of a configuration.
    /// </summary>
    public static async Task<ProxySelectionBlock> ExportAsync(IStateStore store, string config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        await TransportProxyCatalog.For(store).EnsureSelectionAsync(config, ct).ConfigureAwait(false);
        var state = await store.GetTransportProxyStateAsync(ct).ConfigureAwait(false);
        var selection = state.Selections.Single(one => one.Config == config);
        if (selection.Reference is not { } reference)
        {
            return new ProxySelectionBlock(null);
        }

        var local = state.Locals.FirstOrDefault(one => one.Reference == reference);
        var offered = state.Publications.SelectMany(one => one.Records).Where(one => one.Reference == reference)
            .OrderByDescending(one => one.Proxy.Updated).FirstOrDefault();
        var record = local ?? offered;
        var retired = record is null ? state.Retired.LastOrDefault(one => one.Reference == reference) : Metadata(record) with
        {
            Sources = record.Local ? null : [.. state.Publications.Where(one => one.Records.Any(item => item.Reference == reference))
                .Select(one => one.Origin).Where(one => one.Length > 0).Distinct(StringComparer.Ordinal)],
        };
        var definition = local is null ? null : new LocalTransportProxyBlock(local.Proxy, local.Legacy,
            local.LegacyIssuer, local.LegacyPort, local.LegacyOffered);
        var trust = local is null ? state.Trust.SingleOrDefault(one => one.Source == reference.Source) : null;
        return new ProxySelectionBlock(reference, retired, trust, definition);
    }

    /// <summary>
    /// Imports a choice and its local definition at one catalog revision.
    /// </summary>
    public static async Task ApplyAsync(IStateStore store, string config, ProxySelectionBlock? block, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (block is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config))
        {
            throw Invalid();
        }

        var imported = Validate(block);
        for (var attempt = 0; attempt < 32; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var held = await store.GetTransportProxyStateAsync(ct).ConfigureAwait(false);
            var initialized = held.LocalSource.Length == 0 ? held with { LocalSource = Guid.NewGuid().ToString("D") } : held;
            var next = Apply(initialized, config, imported);
            if (held.ToPayload() == next.ToPayload())
            {
                return;
            }

            next = next with { Revision = checked(held.Revision + 1) };
            if (await store.TrySaveTransportProxyStateAsync(next, held.Revision, ct).ConfigureAwait(false))
            {
                return;
            }

            await Task.Yield();
        }

        throw new TransportProxyFault("proxy-catalog-busy");
    }

    private static TransportProxyState Apply(TransportProxyState state, string config, ProxySelectionBlock block)
    {
        if (block.Reference is not { } reference)
        {
            return Select(state, config, null);
        }

        if (block.Local is { } definition)
        {
            var equivalent = state.Locals.FirstOrDefault(one => Equivalent(one, definition, config));
            if (equivalent is not null)
            {
                return Select(state, config, equivalent.Reference);
            }

            var fresh = LocalReference(state);
            var proxy = definition.Proxy with { Id = fresh.Id, Updated = DateTimeOffset.UtcNow };
            var record = new CatalogTransportProxy(fresh, proxy, Local: true, Legacy: definition.Legacy,
                LegacyIssuer: definition.LegacyIssuer, LegacyPort: definition.LegacyPort,
                LegacyPublisher: definition.Legacy ? config : "", LegacyOffered: definition.LegacyOffered);
            return Select(state with { Locals = [.. state.Locals, record] }, config, fresh);
        }

        if (block.Retired is { Local: true } missingLocal)
        {
            var existing = state.Retired.FirstOrDefault(one => one.Local && one.Reference.Source == state.LocalSource
                && !state.Locals.Any(record => record.Reference == one.Reference)
                && (one with { Reference = missingLocal.Reference }) == missingLocal);
            var fresh = existing?.Reference ?? LocalReference(state);
            return Select(Retire(state, missingLocal with { Reference = fresh }), config, fresh);
        }

        if (reference.Source == state.LocalSource)
        {
            throw new TransportProxyFault("proxy-source-conflict");
        }

        if (block.Trust is { } trust)
        {
            var pinned = state.Trust.FirstOrDefault(one => one.Source == trust.Source);
            if (pinned is not null && pinned != trust)
            {
                throw new TransportProxyFault("proxy-source-conflict");
            }

            if (pinned is null)
            {
                state = state with { Trust = [.. state.Trust, trust] };
            }
        }

        if (block.Retired is { } retired)
        {
            state = Retire(state, retired);
        }

        return Select(state, config, reference);
    }

    private static TransportProxyState Select(TransportProxyState state, string config, ProxyReference? reference) => state with
    {
        Selections = [.. state.Selections.Where(one => one.Config != config).Append(new TransportProxySelection(config, reference))
            .OrderBy(one => one.Config, StringComparer.Ordinal)],
    };

    private static TransportProxyState Retire(TransportProxyState state, RetiredTransportProxy retired) => state with
    {
        Retired = [.. state.Retired.Where(one => one.Reference != retired.Reference).Append(retired)
            .OrderBy(one => one.Reference.Source, StringComparer.Ordinal).ThenBy(one => one.Reference.Id, StringComparer.Ordinal)],
    };

    private static ProxyReference LocalReference(TransportProxyState state)
    {
        while (true)
        {
            var reference = new ProxyReference(state.LocalSource, Guid.NewGuid().ToString("N"));
            if (!state.Locals.Any(one => one.Reference == reference) && !state.Retired.Any(one => one.Reference == reference)
                && !state.Selections.Any(one => one.Reference == reference))
            {
                return reference;
            }
        }
    }

    private static RetiredTransportProxy Metadata(CatalogTransportProxy record) => new(record.Reference, record.Proxy.Name,
        record.Proxy.Transport, record.Proxy.Host, record.Proxy.Port, record.Legacy ? "" : record.Proxy.Path, record.Local,
        Http: record.Proxy.Http, Quic: record.Proxy.Quic, UseCertificate: ProxyTransportModes.UsesCertificate(record.Proxy));

    private static bool Equivalent(CatalogTransportProxy record, LocalTransportProxyBlock block, string config) =>
        record.Local && record.Legacy == block.Legacy && record.LegacyIssuer == block.LegacyIssuer
        && record.LegacyPort == block.LegacyPort && record.LegacyOffered == block.LegacyOffered
        && (!record.Legacy || record.LegacyPublisher == config)
        && JsonSerializer.Serialize(record.Proxy with { Id = "", Updated = DateTimeOffset.MinValue }, Json)
            == JsonSerializer.Serialize(block.Proxy with { Id = "", Updated = DateTimeOffset.MinValue }, Json);

    private static ProxySelectionBlock Validate(ProxySelectionBlock block)
    {
        if (block.Reference is null)
        {
            if (block.Local is not null || block.Trust is not null || block.Retired is not null)
            {
                throw Invalid();
            }

            return block;
        }

        var reference = Reference(block.Reference);
        var retired = block.Retired;
        if (retired is not null)
        {
            if (Reference(retired.Reference) != reference || retired.Name is null || retired.Name.Length > 64
                || retired.Transport is not ("" or "websocket" or "xhttp" or "http" or "quic") || retired.Host is null || retired.Host.Length > 253
                || retired.Host.Length > 0 && Uri.CheckHostName(retired.Host) == UriHostNameType.Unknown
                || retired.Port is < 0 or > 65535 || retired.Path is null || retired.Path.Length > 64
                || !retired.Path.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '_')
                || retired.Sources is { Count: > 256 }
                || retired.Sources?.Any(one => string.IsNullOrEmpty(one) || one.Length > 253 || Uri.CheckHostName(one) == UriHostNameType.Unknown) == true
                || retired.Local && retired.Sources is { Count: > 0 })
            {
                throw Invalid();
            }

            retired = retired with { Reference = reference };
        }

        var trust = block.Trust is null ? null : Trust(block.Trust, reference);
        if (block.Local is { } local)
        {
            if (trust is not null || retired is { Local: false } || local.Proxy is null || local.Proxy.Id != reference.Id
                || local.LegacyIssuer is null || local.LegacyPort is < 0 or > 65535
                || local.Proxy.Name is not { Length: > 0 and <= 64 } || string.IsNullOrWhiteSpace(local.Proxy.Name)
                || local.Proxy.Tls is not { ServerName: not null, Pin: not null }
                || local.Proxy.Auth is not { Scheme: not null, Credential: not null })
            {
                throw Invalid();
            }

            if (local.Legacy)
            {
                if (local.LegacyIssuer.Length > 0 && !WgKey(local.LegacyIssuer)
                    || Uri.CheckHostName(local.Proxy.Tls.ServerName) == UriHostNameType.Unknown
                    || local.Proxy.Transport != "websocket" || local.Proxy.Host is not { Length: > 0 and <= 253 }
                    || local.Proxy.Path is null || local.Proxy.Path.Length > 2048 || local.Proxy.Path.Any(char.IsControl)
                    || local.Proxy.Port is < 1 or > 65535
                    || local.Proxy.Tls.Pin.Length > 0 && (local.Proxy.Tls.Pin.Length != 64 || !local.Proxy.Tls.Pin.All(char.IsAsciiHexDigit))
                    || !WsEndpoint.Dials(new WsEndpoint(local.Proxy.Host, local.Proxy.Port, local.Proxy.Path, local.Proxy.Auth.Credential).Address()))
                {
                    throw Invalid();
                }
            }
            else
            {
                if (local.LegacyIssuer.Length > 0 || local.LegacyPort != 0 || local.LegacyOffered
                    || !OfferedProxyCatalog.Valid(local.Proxy) && !StoredPlain(local.Proxy))
                {
                    throw Invalid();
                }
            }
        }
        else if (retired is { Local: true } && trust is not null)
        {
            throw Invalid();
        }

        return block with { Reference = reference, Retired = retired, Trust = trust,
            Local = block.Local is { } held ? held with { Proxy = ProxyTransportModes.Normalize(held.Proxy) } : null };
    }

    private static bool StoredPlain(OfferedProxy proxy)
    {
        if (proxy.Tls.Enabled && !(proxy.Transport == "xhttp" && proxy.Xhttp?.Scheme == "http"))
        {
            return false;
        }

        return OfferedProxyCatalog.Valid(proxy with
        {
            Tls = proxy.Tls with { Enabled = true },
            Xhttp = proxy.Xhttp is { } profile ? profile with { Scheme = "https" } : null,
        });
    }

    private static ProxyReference Reference(ProxyReference? reference)
    {
        if (reference is null || !Guid.TryParse(reference.Source, out var source) || source == Guid.Empty
            || reference.Id is not { Length: > 0 and <= 64 }
            || !reference.Id.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '_'))
        {
            throw Invalid();
        }

        return reference with { Source = source.ToString("D") };
    }

    private static TransportProxyTrust Trust(TransportProxyTrust trust, ProxyReference reference)
    {
        if (!Guid.TryParse(trust.Source, out var source) || source.ToString("D") != reference.Source
            || !Guid.TryParse(trust.AdvertisedSource, out var advertised) || advertised == Guid.Empty
            || trust.Issuer is null || trust.PublicKey is null)
        {
            throw Invalid();
        }

        var normalized = trust with { Source = reference.Source, AdvertisedSource = advertised.ToString("D") };
        if (normalized.Kind == "unsigned" && normalized.PublicKey.Length == 0 && WgKey(normalized.Issuer)
            && normalized.Source == ProxySourceIdentity.Unsigned(normalized.AdvertisedSource, normalized.Issuer))
        {
            return normalized;
        }

        if (normalized.Kind != "signed" || normalized.Issuer.Length > 0 || normalized.PublicKey.Length is 0 or > 512
            || normalized.Source != ProxySourceIdentity.Signed(normalized.AdvertisedSource))
        {
            throw Invalid();
        }

        try
        {
            var publicKey = Convert.FromBase64String(normalized.PublicKey);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            if (consumed != publicKey.Length || verifier.KeySize != 256
                || verifier.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            {
                throw Invalid();
            }

            return normalized with { PublicKey = Convert.ToBase64String(verifier.ExportSubjectPublicKeyInfo()) };
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw Invalid();
        }
    }

    private static bool WgKey(string value)
    {
        if (value.Length != 44)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[32];
        return Convert.TryFromBase64String(value, bytes, out var size) && size == 32 && Convert.ToBase64String(bytes) == value;
    }

    private static TransportProxyFault Invalid() => new("bad-proxy");
}
