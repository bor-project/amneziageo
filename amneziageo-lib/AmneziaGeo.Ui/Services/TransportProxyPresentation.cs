using AmneziaGeo.Decl;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.Services;

internal static class TransportProxyPresentation
{
    public static bool Matches(TransportProxySummary summary, ProxyReference? reference) => reference is not null
        && (summary.Reference == reference || summary.Aliases?.Contains(reference) == true);

    public static TransportProxySummary? FindExact(IReadOnlyList<TransportProxySummary> summaries, ProxyReference? reference) =>
        summaries.FirstOrDefault(one => one.Reference == reference);

    public static TransportProxySummary? FindRunning(IReadOnlyList<TransportProxySummary> summaries, ProxyReference? reference) =>
        summaries.FirstOrDefault(one => !one.Missing && one.Reference == reference)
        ?? summaries.FirstOrDefault(one => !one.Missing && one.Aliases?.Contains(reference!) == true)
        ?? FindExact(summaries, reference);

    public static string Transport(string transport) => transport switch
    {
        "xhttp" => "XHTTP",
        "websocket" => "WebSocket",
        _ => transport,
    };

    public static string Source(bool local, IEnumerable<string> sources) => local ? Loc.Instance.Get("Proxies_Local")
        : string.Join(", ", sources.Where(one => one.Length > 0).Distinct(StringComparer.Ordinal)) is { Length: > 0 } text
            ? text : Loc.Instance.Get("Proxies_Server");

    public static string Details(TransportProxySummary summary) => string.Join(" · ", summary.Name, Transport(summary.Transport),
        Source(summary.Local, summary.Sources), TransportProxyWords.Address(summary.Host, summary.Port, summary.Path));

    public static string Selected(IReadOnlyList<TransportProxySummary> summaries, ProxyReference? reference) => reference is null
        ? Loc.Instance.Get("Proxies_None") : FindExact(summaries, reference) is { } summary
            ? Details(summary) + (summary.Missing ? " · " + Loc.Instance.Get("Proxies_Missing") : string.Empty)
        : $"{reference.Id} · {reference.Source} · {Loc.Instance.Get("Proxies_Missing")}";

    public static string Running(IReadOnlyList<TransportProxySummary> summaries, RunningTransportProxy? actual)
    {
        if (actual is null)
        {
            return Loc.Instance.Get("Proxies_ActualUnknown");
        }

        var target = Endpoint(actual.TargetHost, actual.TargetPort);
        if (actual.Reference is null)
        {
            return $"UDP · {target}";
        }

        var summary = FindRunning(summaries, actual.Reference);
        if (actual.Front is not { } front)
        {
            return $"{summary?.Name ?? actual.Reference.Id} · {actual.Reference.Source} → {target}";
        }

        return string.Join(" · ", summary?.Name ?? front.Name, Transport(front.Transport), Source(front.Local, [front.Source]),
            TransportProxyWords.Address(front.Host, front.Port, front.Path)) + $" → {target}";
    }

    public static string Endpoint(string host, int port) => $"{(host.Contains(':') ? $"[{host}]" : host)}:{port}";

    public static string Scope(TransportProxySummary summary) => Loc.Instance.Get(summary.Legacy ? "Proxies_ScopeLegacy"
        : summary.Targets?.Restricted == true ? "Proxies_ScopeRestricted" : "Proxies_ScopeAny");

    public static string ScopeTargets(TransportProxySummary summary) => summary.Targets?.Restricted == true
        ? string.Join(", ", summary.Targets.Destinations.Select(one => Endpoint(one.Host, one.Port))) : string.Empty;
}
