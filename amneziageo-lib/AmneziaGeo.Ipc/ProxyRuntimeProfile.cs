using System.Net;
using System.Text.Json;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Builds HTTP and QUIC carrier profiles.
/// </summary>
public static class ProxyRuntimeProfile
{
    public const string RuntimeVersion = "HTTP GET 1 transports-v2 GET POST QUIC";

    public static string Create(ResolvedTransportProxy selected, int localPort, IPAddress dialAddress)
    {
        if (selected.Record.Legacy || localPort is < 1 or > 65535 || selected.TargetPort is < 1 or > 65535
            || Uri.CheckHostName(selected.TargetHost) == UriHostNameType.Unknown)
        {
            throw new TransportProxyFault("bad-proxy");
        }

        return Write(selected.Record.Proxy, dialAddress, localPort, selected.TargetHost, selected.TargetPort);
    }

    public static string Probe(OfferedProxy proxy, IPAddress dialAddress)
    {
        if (proxy.Transport != "quic")
        {
            throw new TransportProxyFault("bad-proxy");
        }

        return Write(proxy, dialAddress, 0, "", 0);
    }

    private static string Write(OfferedProxy proxy, IPAddress dialAddress, int localPort, string targetHost, int targetPort)
    {
        if (!OfferedProxyCatalog.Valid(proxy) || proxy.Transport is not ("http" or "xhttp" or "quic"))
        {
            throw new TransportProxyFault("bad-proxy");
        }

        return JsonSerializer.Serialize(new
        {
            version = 2,
            transport = proxy.Transport == "xhttp" ? "http" : proxy.Transport,
            listen = new { host = localPort == 0 ? "" : "127.0.0.1", port = localPort },
            target = new { host = targetHost, port = targetPort },
            front = new
            {
                proxy.Host, proxy.Port, proxy.Path, dialAddress = dialAddress.ToString(),
                tls = proxy.Tls with { Enabled = ProxyTransportModes.UsesCertificate(proxy) },
            },
            auth = ProxyTransportModes.IsHttp(proxy)
                ? new OfferedProxyAuth("hmac-sha256-v2", Guid.Parse(proxy.Auth.Credential).ToString("D")) : proxy.Auth,
            http = ProxyTransportModes.IsHttp(proxy) ? proxy.Http ?? new OfferedProxyHttp(2, "GET", "header") : null,
            quic = proxy.Quic,
        }, IpcJson.Options);
    }
}
