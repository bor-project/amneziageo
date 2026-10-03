using AmneziaGeo.Decl;
using AmneziaGeo.Geo;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// A connecting user's data scope: their store, config repository, geo configurator and what their servers offer.
/// </summary>
internal sealed class BrokerScope(string userRoot, IStateStore store, ConfigRepository configRepo, GeoConfigurator geo, ServerOffers offers)
{
    private int _used;

    /// <summary>
    /// What the servers of the user's configurations offer.
    /// </summary>
    public ServerOffers Offers => offers;

    /// <summary>
    /// The user's data root.
    /// </summary>
    public string UserRoot => userRoot;

    /// <summary>
    /// The user's SID, or null when unresolved.
    /// </summary>
    public string? Sid { get; set; }

    /// <summary>
    /// The user's composite store.
    /// </summary>
    public IStateStore Store => store;

    /// <summary>
    /// A config repository over the user's store.
    /// </summary>
    public ConfigRepository ConfigRepo => configRepo;

    /// <summary>
    /// A geo configurator over the user's store.
    /// </summary>
    public GeoConfigurator Geo => geo;

    /// <summary>
    /// Tells the first caller only that the scope is new to this run, so what a fresh library is given goes in once.
    /// </summary>
    public bool FirstUse() => Interlocked.Exchange(ref _used, 1) == 0;
}
