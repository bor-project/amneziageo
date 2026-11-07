using System.Text.Json;

namespace AmneziaGeo.Decl;

/// <summary>
/// Refreshes the server proxy of one running session.
/// </summary>
public sealed record TransportProxyRefresh(string SessionId, string Config, ProxyReference? Reference,
    ResolvedTransportProxy? Proxy, string Fault = "", ProxyReference? CanonicalReference = null,
    bool LegacyWebSocket = false, WsEndpoint? Front = null)
{
    /// <summary>
    /// Identifies a new explicit connection.
    /// </summary>
    public static string NewSessionId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Selects the durable session on redelivery.
    /// </summary>
    public static T? RequestForStart<T>(T? carried, T? stored, bool redelivery) where T : class =>
        redelivery ? stored : carried ?? stored;

    /// <summary>
    /// Matches the session and running reference.
    /// </summary>
    public bool AppliesTo(string sessionId, string config, ProxyReference? reference) =>
        !LegacyWebSocket && SessionId is { Length: > 0 } && SessionId == sessionId && Config == config && Reference is not null
        && Reference == reference && (Fault is { Length: > 0 } && Proxy is null
            || Fault == "" && Proxy is not null && Proxy.Record.Reference == (CanonicalReference ?? Reference)
                && (CanonicalReference is null || CanonicalReference.Id == Reference.Id));

    /// <summary>
    /// Applies the definition or preserves the selected proxy with its fault.
    /// </summary>
    public (ResolvedTransportProxy Proxy, string Fault) ApplyTo(string sessionId, string config,
        ResolvedTransportProxy running, string previousFault = "") =>
        !AppliesTo(sessionId, config, running.Record.Reference) ? (running, previousFault)
            : Fault.Length > 0 ? (running, Fault) : (Proxy!, "");

    /// <summary>
    /// Restarts a connecting carrier when its transport changes.
    /// </summary>
    public static bool ChangesDial(bool connecting, string sessionId, string config, ResolvedTransportProxy? dialing,
        string fault, string currentSessionId, string currentConfig, ResolvedTransportProxy? current, string currentFault,
        WsEndpoint? dialingFront = null, WsEndpoint? currentFront = null) =>
        connecting && !string.IsNullOrEmpty(sessionId) && sessionId == currentSessionId && config == currentConfig
            && (dialing is not null && current is not null
                && (fault != currentFault || !SameTransport(dialing, current))
                || dialing is null && current is null && dialingFront is not null && dialingFront != currentFront);

    /// <summary>
    /// Reads the WebSocket carried by an older session.
    /// </summary>
    public static WsEndpoint? LegacyFront(string? host, int port, bool offered) =>
        host is { Length: > 0 } && port > 0
            ? WsEndpoint.Of(host, port, "", "") with { Offered = offered } : null;

    /// <summary>
    /// Matches an older WebSocket session.
    /// </summary>
    public bool AppliesToLegacy(string sessionId, string config, ResolvedTransportProxy? running, WsEndpoint? front) =>
        LegacyWebSocket && Reference is null && Proxy is null && Fault == "" && Front is not null && SessionId is { Length: > 0 }
            && SessionId == sessionId && Config == config && running is null && front is not null;

    /// <summary>
    /// Refreshes the offered front of an older WebSocket session.
    /// </summary>
    public static TransportProxyRefresh? CreateLegacy(string sessionId, string config, string text,
        ResolvedTransportProxy? running, WsEndpoint? front, ServerOffer? offer)
    {
        if (string.IsNullOrEmpty(sessionId) || running is not null || front is null || offer is not { Ours: true })
        {
            return null;
        }

        var held = WsEndpoint.Of(text, offer, null);
        return held is not null ? new TransportProxyRefresh(sessionId, config, null, null, LegacyWebSocket: true, Front: held) : null;
    }

    private static bool SameTransport(ResolvedTransportProxy first, ResolvedTransportProxy second)
    {
        var left = first.Record.Proxy;
        var right = second.Record.Proxy;
        return first.TargetPort == second.TargetPort && SameHost(first.TargetHost, second.TargetHost)
            && first.Record.Legacy == second.Record.Legacy && first.Record.LegacyOffered == second.Record.LegacyOffered
            && left.Transport == right.Transport && SameHost(left.Host, right.Host) && left.Port == right.Port
            && left.Path == right.Path && SameHost(left.Tls.ServerName, right.Tls.ServerName)
            && string.Equals(left.Tls.Pin, right.Tls.Pin, StringComparison.OrdinalIgnoreCase)
            && left.Auth == right.Auth && left.Xhttp == right.Xhttp;
    }

    private static bool SameHost(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the current server definition without changing the saved choice.
    /// </summary>
    public static async Task<TransportProxyRefresh?> CreateAsync(IStateStore store, string sessionId, string config,
        string text, ResolvedTransportProxy? running, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId) || running is null || running.Record.Local)
        {
            return null;
        }

        var reference = running.Record.Reference;
        try
        {
            var current = await TransportProxyCatalog.For(store).ResolveRunningAsync(config, text, reference, ct).ConfigureAwait(false);
            return new TransportProxyRefresh(sessionId, config, reference,
                current with { TargetHost = running.TargetHost, TargetPort = running.TargetPort },
                CanonicalReference: current.Record.Reference != reference ? current.Record.Reference : null);
        }
        catch (TransportProxyFault ex)
        {
            return new TransportProxyRefresh(sessionId, config, reference, null, ex.Code);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return new TransportProxyRefresh(sessionId, config, reference, null, "proxy-catalog-invalid");
        }
    }

    /// <summary>
    /// Omits transport credentials from diagnostics.
    /// </summary>
    public override string ToString() => $"{Config}: {Reference?.Source}/{Reference?.Id}";
}
