using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Reverts leftover network mutations from a previous tunnel session.
/// </summary>
internal sealed class NetworkReconciler(DnsConfigurator dns, RouteManager routes, ILogger<NetworkReconciler> logger)
{
    /// <summary>
    /// Restores persisted DNS and removes persisted exclusion routes.
    /// </summary>
    public void Reconcile()
    {
        dns.RestoreSaved();
        routes.RestoreSavedExclusions();
        routes.RestoreSavedLanExclusions();
        logger.LogDebug("network state reconciled");
    }
}
