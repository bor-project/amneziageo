using AmneziaGeo.Routing;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Hands the running session's live state to readers outside the data path - the runtime inspector reports on it
/// and a rule edit re-applies through it. Every slot is empty between sessions, and each is filled only when the
/// session builds that piece: the cache only for an on-demand Direct bucket, the tracker only in split mode with
/// domain rules, the proxy only when it bound its port.
/// </summary>
internal sealed class LiveSession
{
    private volatile RoutingCache? _cache;
    private volatile DomainTracker? _tracker;
    private volatile DnsProxy? _proxy;
    private volatile bool _namesRedirected;

    /// <summary>
    /// Per-destination verdict cache of the session in flight.
    /// </summary>
    public RoutingCache? Cache => _cache;

    /// <summary>
    /// Domain tracker of the session in flight.
    /// </summary>
    public DomainTracker? Tracker => _tracker;

    /// <summary>
    /// Name proxy of the session in flight.
    /// </summary>
    public DnsProxy? Proxy => _proxy;

    /// <summary>
    /// Whether the adapters were pointed at this session's proxy. Until they are, names are resolved outside it
    /// and rules by domain do not apply.
    /// </summary>
    public bool NamesRedirected => _namesRedirected;

    /// <summary>
    /// Publishes the session's verdict cache.
    /// </summary>
    public void SetCache(RoutingCache? cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// Publishes the session's domain tracker.
    /// </summary>
    public void SetTracker(DomainTracker? tracker)
    {
        _tracker = tracker;
    }

    /// <summary>
    /// Publishes the session's name proxy.
    /// </summary>
    public void SetProxy(DnsProxy? proxy)
    {
        _proxy = proxy;
    }

    /// <summary>
    /// Records that the adapters now send their lookups to the session's proxy.
    /// </summary>
    public void SetNamesRedirected(bool redirected)
    {
        _namesRedirected = redirected;
    }

    /// <summary>
    /// Drops every slot at teardown.
    /// </summary>
    public void Clear()
    {
        _cache = null;
        _tracker = null;
        _proxy = null;
        _namesRedirected = false;
    }
}
