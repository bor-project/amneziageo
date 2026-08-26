using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Builds a runner per tunnel, so every desired configuration is supervised on its own.
/// </summary>
internal sealed class ConfigRunnerFactory(
    ServiceManager serviceManager,
    UapiClient uapi,
    NetworkReconciler reconciler,
    SettingsStore settingsStore,
    AgentControl control,
    ScopedStoreFactory stores,
    RoutingDistributor distributor,
    ILoggerFactory loggerFactory)
{
    /// <summary>
    /// Returns a runner bound to one tunnel.
    /// </summary>
    public ConfigRunner Create(TunnelControl tunnel)
    {
        return new ConfigRunner(
            tunnel,
            serviceManager,
            uapi,
            reconciler,
            settingsStore,
            control,
            stores,
            distributor,
            loggerFactory.CreateLogger<ConfigRunner>());
    }
}
