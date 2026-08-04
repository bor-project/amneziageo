using AmneziaGeo.Routing;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Hands the running session's live state to readers outside the data path - the runtime inspector reports on it,
/// nothing else touches it. Both slots are empty between sessions, and each is filled only when the session builds
/// that piece: the cache only for an on-demand Direct bucket, the tracker only in split mode with domain rules.
/// </summary>
internal sealed class LiveSession
{
    private volatile RoutingCache? _cache;
    private volatile DomainTracker? _tracker;

    /// <summary>
    /// Per-destination verdict cache of the session in flight.
    /// </summary>
    public RoutingCache? Cache => _cache;

    /// <summary>
    /// Domain tracker of the session in flight.
    /// </summary>
    public DomainTracker? Tracker => _tracker;

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
    /// Drops both slots at teardown.
    /// </summary>
    public void Clear()
    {
        _cache = null;
        _tracker = null;
    }
}
