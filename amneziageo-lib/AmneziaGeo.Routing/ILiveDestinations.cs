namespace AmneziaGeo.Routing;

/// <summary>
/// Destinations live right now: every remote, and the subset owned by a process the app rules cover. The second
/// set exists because a destination decided without it is decided wrongly: an address an app reached without a
/// name lookup looks like ordinary traffic and earns a permit out the physical path, which no later contact undoes.
/// </summary>
public sealed record LiveDestinations(HashSet<uint> All, HashSet<uint> App);

/// <summary>
/// The destinations the host is talking to right now. Separated from the cache because how a platform learns this
/// differs: one enumerates a connection table, another sees every packet and already knows.
/// </summary>
public interface ILiveDestinations
{
    /// <summary>
    /// Remote addresses of every current connection, host order, and those an app rule covers. Feeds both the
    /// routing pass - which is how an inbound connection and one already established at bring-up earn their route -
    /// and the reclaim pass. The app set is empty where the platform cannot tie a socket to a process.
    /// </summary>
    LiveDestinations Snapshot();
}
