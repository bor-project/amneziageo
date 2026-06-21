using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Reverts leftover system network mutations — redirected NIC DNS and endpoint-exclusion routes —
/// from a previous tunnel session, including one that exited without running its teardown (crash,
/// watchdog kill, or kill-switch sever). Run on agent startup and before bringing a tunnel up so a
/// dead predecessor cannot leak DNS or routing state into the next session and break switching.
/// </summary>
internal sealed class NetworkReconciler(DnsConfigurator dns, RouteManager routes, ILogger<NetworkReconciler> logger)
{
    /// <summary>
    /// Restores any persisted DNS redirect and removes any persisted endpoint-exclusion routes.
    /// Idempotent and a no-op when nothing was left behind.
    /// </summary>
    public void Reconcile()
    {
        dns.RestoreSaved();
        routes.RestoreSavedExclusions();
        routes.RestoreSavedLanExclusions();
        logger.LogDebug("network state reconciled");
    }
}
