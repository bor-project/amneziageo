using System.Net;
using System.Text.Json;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Builds the GET transport profile for HTTP GET.
/// </summary>
public static class XHttpProfile
{
    public const string Version = "1";

    /// <summary>
    /// Creates a UDP tunnel through the selected XHTTP proxy.
    /// </summary>
    public static string Create(ResolvedTransportProxy selected, int localPort, IPAddress dialAddress)
    {
        var proxy = ProxyTransportModes.GetRuntime(selected.Record.Proxy);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        if (selected.Record.Legacy || localPort is < 1 or > 65535 || selected.TargetPort is < 1 or > 65535
            || Uri.CheckHostName(selected.TargetHost) == UriHostNameType.Unknown)
        {
            throw new TransportProxyFault("bad-proxy");
        }

        var profile = proxy.Xhttp!;
        var tls = new Dictionary<string, object>
        {
            ["serverName"] = proxy.Tls.ServerName,
            ["alpn"] = new[] { "h2" },
            ["fingerprint"] = "unsafe",
        };
        if (proxy.Tls.Pin.Length > 0)
        {
            tls["pinnedPeerCertSha256"] = proxy.Tls.Pin.ToLowerInvariant();
        }

        return JsonSerializer.Serialize(new
        {
            log = new { loglevel = "warning" },
            inbounds = new[]
            {
                new
                {
                    tag = "wg-udp", listen = "127.0.0.1", port = localPort, protocol = "tunnel",
                    settings = new { allowedNetwork = "udp", rewriteAddress = selected.TargetHost, rewritePort = selected.TargetPort },
                },
            },
            outbounds = new[]
            {
                new
                {
                    tag = "xhttp-get", protocol = "vless",
                    settings = new { address = dialAddress.ToString(), port = proxy.Port, id = Guid.Parse(proxy.Auth.Credential).ToString("D"), encryption = "none" },
                    streamSettings = new
                    {
                        network = "xhttp", security = "tls", tlsSettings = tls,
                        xhttpSettings = new
                        {
                            host = proxy.Host, path = "/" + proxy.Path + "/", mode = "packet-up",
                            extra = new
                            {
                                uplinkHTTPMethod = "GET", uplinkDataPlacement = "header", uplinkDataKey = "X-Data",
                                uplinkChunkSize = profile.UplinkChunkSize, scMaxEachPostBytes = profile.ScMaxEachPostBytes,
                                scMinPostsIntervalMs = profile.ScMinPostsIntervalMs, xPaddingBytes = profile.XPaddingBytes,
                            },
                        },
                    },
                },
            },
        }, options);
    }
}
