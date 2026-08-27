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
    /// <paramref name="keep"/> names the tunnels whose records are left in place.
    /// </summary>
    public void Reconcile(Func<bool>? abortIf = null, IEnumerable<string>? keep = null)
    {
        Step(() => dns.RestoreSaved(abortIf, keep), "dns restore");
        Step(() => routes.RestoreSavedExclusions(abortIf, keep), "route exclusion restore");
        Step(() => routes.RestoreSavedLanExclusions(abortIf, keep), "lan exclusion restore");
        logger.LogDebug("network state reconciled");
    }

    /// <summary>
    /// Reverts what the named tunnel left behind and leaves every other tunnel's records in place.
    /// </summary>
    public void Reconcile(string tunnel)
    {
        Step(() => dns.Restore(tunnel), "dns restore");
        Step(() => routes.RemoveEndpointExclusions(tunnel), "route exclusion restore");
        Step(() => routes.RemoveLanExclusions(tunnel), "lan exclusion restore");
        logger.LogDebug("network state of {Tunnel} reconciled", tunnel);
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
            logger.LogWarning(ex, "cleaning up after an earlier session failed at the step '{What}'; leftover routes or DNS settings may remain until the next start", what);
        }
    }
}
