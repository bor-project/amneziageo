using AmneziaGeo.Decl;
using AmneziaGeo.Geo;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// A connecting user's data scope: their store, config repository, and geo configurator.
/// </summary>
internal sealed class BrokerScope(string userRoot, IStateStore store, ConfigRepository configRepo, GeoConfigurator geo)
{
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
}
