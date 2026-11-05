using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AmneziaGeo.Decl;

/// <summary>
/// Stores a transport proxy and its destination restrictions.
/// </summary>
public sealed record CatalogTransportProxy(ProxyReference Reference, OfferedProxy Proxy,
    bool Local = false, bool Legacy = false, string LegacyIssuer = "", int LegacyPort = 0, string LegacyPublisher = "", bool LegacyOffered = false);

/// <summary>
/// Stores a verified publication of one configuration.
/// </summary>
public sealed record TransportProxyPublication(string Publisher, string Mark, string Issuer, string Source,
    IReadOnlyList<CatalogTransportProxy> Records, string Origin = "", string Error = "", string Slot = "catalog");

/// <summary>
/// Pins the identity of a proxy source.
/// </summary>
public sealed record TransportProxyTrust(string Source, string Kind, string PublicKey = "", string Issuer = "", string AdvertisedSource = "");

/// <summary>
/// Stores the explicit proxy choice of a configuration.
/// </summary>
public sealed record TransportProxySelection(string Config, ProxyReference? Reference);

/// <summary>
/// Retains the public metadata of a withdrawn proxy.
/// </summary>
public sealed record RetiredTransportProxy(ProxyReference Reference, string Name, string Transport, string Host, int Port, string Path, bool Local,
    IReadOnlyList<string>? Sources = null);

/// <summary>
/// Stores the common catalog and configuration choices.
/// </summary>
public sealed record TransportProxyState
{
    public int Version { get; init; } = 1;
    public long Revision { get; init; }
    public string LocalSource { get; init; } = "";
    public IReadOnlyList<CatalogTransportProxy> Locals { get; init; } = [];
    public IReadOnlyList<TransportProxyPublication> Publications { get; init; } = [];
    public IReadOnlyList<TransportProxyTrust> Trust { get; init; } = [];
    public IReadOnlyList<TransportProxySelection> Selections { get; init; } = [];
    public IReadOnlyList<RetiredTransportProxy> Retired { get; init; } = [];

    public static TransportProxyState Empty { get; } = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the stored catalog.
    /// </summary>
    public static TransportProxyState Parse(string payload)
    {
        var state = JsonSerializer.Deserialize<TransportProxyState>(payload, Json)
            ?? throw new InvalidDataException("The transport proxy catalog is empty.");
        if (state.Version != 1 || state.Revision < 0 || state.LocalSource is null
            || state.LocalSource.Length > 0 && !Guid.TryParse(state.LocalSource, out _)
            || state.Locals is null || state.Publications is null || state.Trust is null || state.Selections is null || state.Retired is null)
        {
            throw new InvalidDataException("The transport proxy catalog is invalid.");
        }

        return state;
    }

    /// <summary>
    /// Serializes the stored catalog.
    /// </summary>
    public string ToPayload() => JsonSerializer.Serialize(this, Json);
}

/// <summary>
/// Describes a proxy without its credential.
/// </summary>
public sealed record TransportProxySummary(ProxyReference Reference, string Name, string Transport, string Host, int Port, string Path,
    bool Local, IReadOnlyList<string> Sources, TransportProxyAvailability Availability = TransportProxyAvailability.Unknown,
    string Fault = "", DateTimeOffset? Checked = null, bool Missing = false);

/// <summary>
/// Describes the proxy selected for a UDP destination.
/// </summary>
public sealed record ResolvedTransportProxy(CatalogTransportProxy Record, string Publisher, string TargetHost, int TargetPort);

/// <summary>
/// Stores catalogs through generic settings.
/// </summary>
public static class TransportProxyStateFallback
{
    private const string Key = "transport-proxy-state-v1";
    private static readonly ConditionalWeakTable<IStateStore, SemaphoreSlim> Gates = new();

    /// <summary>
    /// Reads a catalog from settings.
    /// </summary>
    public static async Task<TransportProxyState> ReadAsync(IStateStore store, CancellationToken ct)
    {
        var payload = await store.GetSettingAsync(Key, ct).ConfigureAwait(false);
        return payload is null ? TransportProxyState.Empty : TransportProxyState.Parse(payload);
    }

    /// <summary>
    /// Replaces a catalog at the expected revision.
    /// </summary>
    public static async Task<bool> SaveAsync(IStateStore store, TransportProxyState state, long expectedRevision, CancellationToken ct)
    {
        if (expectedRevision < 0 || state.Revision != checked(expectedRevision + 1))
        {
            throw new ArgumentException("The transport proxy revision must advance by one.", nameof(state));
        }

        var gate = Gates.GetValue(store, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var held = await ReadAsync(store, ct).ConfigureAwait(false);
            if (held.Revision != expectedRevision)
            {
                return false;
            }

            await store.SetSettingAsync(Key, state.ToPayload(), ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }
}
