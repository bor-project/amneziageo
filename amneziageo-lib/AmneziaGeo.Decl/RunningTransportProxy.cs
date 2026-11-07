namespace AmneziaGeo.Decl;

/// <summary>
/// Names the transport and UDP destination of a running session.
/// </summary>
public sealed record RunningTransportProxy(ProxyReference? Reference, string TargetHost, int TargetPort, string ServerKey = "",
    RunningProxyFront? Front = null)
{
    /// <summary>
    /// Captures the session transport.
    /// </summary>
    public static RunningTransportProxy From(ResolvedTransportProxy? proxy, string config) => new(proxy?.Record.Reference,
        proxy?.TargetHost ?? ConfigServices.Host(config), proxy?.TargetPort ?? ConfigServices.EndpointPort(config),
        ConfigServices.Target(config)?.ServerKey ?? string.Empty,
        proxy is null ? null : new RunningProxyFront(proxy.Record.Proxy.Name, proxy.Record.Proxy.Transport,
            proxy.Record.Proxy.Host, proxy.Record.Proxy.Port, proxy.Record.Legacy ? "" : proxy.Record.Proxy.Path,
            proxy.Record.Local, proxy.Origin));
}

/// <summary>
/// Describes the public front of a running proxy.
/// </summary>
public sealed record RunningProxyFront(string Name, string Transport, string Host, int Port, string Path, bool Local, string Source);
