using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.Services;

internal static class TransportProxyWords
{
    public static string Address(string host, int port, string path) =>
        $"{(host.Contains(':') ? $"[{host}]" : host)}:{port}/{path.TrimStart('/')}";

    public static string Availability(TransportProxyAvailability availability, bool missing = false) =>
        Loc.Instance.Get(missing ? "Proxies_Missing" : availability switch
        {
            TransportProxyAvailability.Available => "Proxies_Available",
            TransportProxyAvailability.Unavailable => "Proxies_Unavailable",
            _ => "Proxies_Unknown",
        });

    public static string Fault(string code) => Loc.Instance.Get(code switch
    {
        "bad-proxy" or "bad-proxy-command" or "bad-proxy-reference" => "Proxies_Invalid",
        "proxy-missing" or "proxy-withdrawn" => "Proxies_Missing",
        "proxy-not-local" => "Proxies_NotLocal",
        "proxy-legacy-target" => "Proxies_LegacyTarget",
        "proxy-catalog-busy" => "Proxies_Busy",
        "proxy-catalog-invalid" => "Proxies_InvalidCatalog",
        "proxy-probe-unsupported" or "proxy-platform-unsupported" or "proxy-unsupported" => "Proxies_Unsupported",
        "proxy-source-conflict" or "proxy-source-invalid" or "proxy-source-downgrade" => "Proxies_IdentityFault",
        "xhttp-runtime-missing" or "xhttp-runtime-version" => "Proxies_XhttpUnavailable",
        "xhttp-runtime-start" or "xhttp-runtime-exited" or "xhttp-runtime-timeout" or "xhttp-version-timeout" => "Proxies_XhttpFailed",
        "proxy-credential-missing" or "proxy-auth" or "proxy-auth-failed" => "Proxies_AuthFault",
        "proxy-no-address" => "Proxies_NoAddress",
        "proxy-no-answer" => "Proxies_NoAnswer",
        "proxy-tls" => "Proxies_TlsFault",
        "proxy-unavailable" => "Proxies_Unavailable",
        "proxy-bad-answer" => "Proxies_BadAnswer",
        "unknown-config" => "Proxies_ConfigMissing",
        "bad-endpoint" => "Proxies_EndpointFault",
        _ => "Proxies_RequestFailed",
    });

    public static T? Read<T>(IpcAck ack)
    {
        if (!ack.Ok)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(ack.Message, IpcJson.Options);
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Trace.TraceError("{0}", ex);
            return default;
        }
    }
}
