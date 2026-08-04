namespace AmneziaGeo.Routing;

/// <summary>
/// The destinations the host is talking to right now. Separated from the cache because how a platform learns this
/// differs: one enumerates a connection table, another sees every packet and already knows.
/// </summary>
public interface ILiveDestinations
{
    /// <summary>
    /// Remote addresses of every current connection, host order. Feeds both the routing pass - which is how an
    /// inbound connection and one already established at bring-up earn their route - and the reclaim pass.
    /// </summary>
    HashSet<uint> Snapshot();
}
