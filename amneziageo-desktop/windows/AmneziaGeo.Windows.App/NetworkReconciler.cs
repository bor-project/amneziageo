using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Reverts leftover network mutations from a previous tunnel session.
/// </summary>
internal sealed class NetworkReconciler(DnsConfigurator dns, RouteManager routes, ILogger<NetworkReconciler> logger)
{
    /// <summary>
    /// Restores persisted DNS and removes persisted exclusion routes. <paramref name="abortIf"/> (the boot path
    /// only) stands the cleanup down the moment a tunnel bring-up is requested, so it never reverts a live tunnel.
    /// </summary>
    public void Reconcile(Func<bool>? abortIf = null)
    {
        Step(() => dns.RestoreSaved(abortIf), "dns restore");
        Step(() => routes.RestoreSavedExclusions(abortIf), "route exclusion restore");
        Step(() => routes.RestoreSavedLanExclusions(abortIf), "lan exclusion restore");
        logger.LogDebug("network state reconciled");
    }

    // Fault-isolate each step: a WMI/IP-helper hiccup must not fault boot startup or skip the later restores.
    private void Step(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "reconcile step failed: {What}", what);
        }
    }
}
