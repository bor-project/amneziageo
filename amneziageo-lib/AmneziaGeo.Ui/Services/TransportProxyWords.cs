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

    public static string Fault(string code, TransportProxyDiagnostic? diagnostic = null)
    {
        var decoded = TransportProxyDiagnostic.Decode(code);
        if ((diagnostic ?? decoded.Diagnostic) is { Valid: true } detail)
        {
            var stage = Loc.Instance.Get("Proxies_Stage_" + detail.Stage);
            var reason = detail.Reason == "http-status" ? HttpFailure(decoded.Code, detail.Status)
                : Loc.Instance.Get("Proxies_Reason_" + detail.Reason, detail.Field);
            return Loc.Instance.Get("Proxies_Diagnostic", stage, reason);
        }

        return BasicFault(decoded.Code);
    }

    private static string HttpFailure(string code, int status)
    {
        var key = status is 400 or 401 or 403 or 404 or 405 or 408 or 426 or 429 or 500 or 502 or 503 or 504
            ? "Proxies_Http_" + status : "Proxies_Http_other";
        var result = Loc.Instance.Get("Proxies_DetailHttp", status, Loc.Instance.Get(key));
        return code is "proxy-target-denied" or "bad-proxy-target" ? BasicFault(code) + " " + result : result;
    }

    private static string BasicFault(string code) => Loc.Instance.Get(code switch
    {
        "bad-proxy" or "bad-proxy-command" or "bad-proxy-reference" => "Proxies_Invalid",
        "proxy-missing" or "proxy-withdrawn" => "Proxies_Missing",
        "proxy-not-local" => "Proxies_NotLocal",
        "proxy-legacy-target" => "Proxies_LegacyTarget",
        "proxy-rejected" => "Proxies_Rejected",
        "proxy-target-denied" => "Proxies_TargetDenied",
        "bad-proxy-targets" or "bad-proxy-target" => "Proxies_InvalidTargets",
        "proxy-catalog-busy" => "Proxies_Busy",
        "proxy-catalog-invalid" => "Proxies_InvalidCatalog",
        "proxy-probe-unsupported" or "proxy-platform-unsupported" or "proxy-unsupported" or "proxy-version-unconfirmed" => "Proxies_Unsupported",
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
        "hello-no-answer" => "Proxies_HelloNoAnswer",
        "hello-timeout" => "Proxies_HelloTimeout",
        "hello-auth" => "Proxies_HelloAuth",
        "hello-bad-answer" => "Proxies_HelloBadAnswer",
        "hello-unavailable" => "Proxies_HelloUnavailable",
        "hello-tls" => "Proxies_TlsFault",
        "hello-refresh-failed" => "Proxies_HelloFailed",
        _ => "Proxies_RequestFailed",
    });

    public static ProxyCatalogRefreshResult? RefreshResult(IpcAck ack)
    {
        if (ack.Ok || !ack.Message.StartsWith('{'))
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<ProxyCatalogRefreshResult>(ack.Message, IpcJson.Options);
            return result is { Items: not null, Failures.Count: > 0 } ? result : null;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Trace.TraceError("{0}", ex);
            return null;
        }
    }

    public static string RefreshFault(ProxyCatalogRefreshResult result) => string.Join(Environment.NewLine,
        result.Failures.Select(one => Loc.Instance.Get("Proxies_RefreshSourceFailed", one.Config, Fault(one.Code))));

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
