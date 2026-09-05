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
    private volatile string _mode = string.Empty;
    private volatile string _listName = string.Empty;
    private volatile IReadOnlyList<string> _configRoutes = [];

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
    /// How the session routes: by the list, everything through the tunnel, or by the configuration alone.
    /// </summary>
    public string Mode => _mode;

    /// <summary>
    /// Routing list in force, empty when none is assigned.
    /// </summary>
    public string ListName => _listName;

    /// <summary>
    /// AllowedIPs of the configuration, which is what decides while routing is off.
    /// </summary>
    public IReadOnlyList<string> ConfigRoutes => _configRoutes;

    /// <summary>
    /// Publishes what the session routes by.
    /// </summary>
    public void SetPlan(string mode, string listName, IReadOnlyList<string> configRoutes)
    {
        _mode = mode;
        _listName = listName;
        _configRoutes = configRoutes;
    }

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
    /// Drops every slot at teardown.
    /// </summary>
    public void Clear()
    {
        _cache = null;
        _tracker = null;
        _proxy = null;
        _mode = string.Empty;
        _listName = string.Empty;
        _configRoutes = [];
    }
}
