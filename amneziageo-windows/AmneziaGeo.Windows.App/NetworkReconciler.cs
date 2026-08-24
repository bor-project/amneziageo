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
        Revert(null, abortIf);
    }

    /// <summary>
    /// Restores what one tunnel changed, leaving every other tunnel's records alone.
    /// </summary>
    public void Reconcile(string tunnel)
    {
        Revert(tunnel, null);
    }

    private void Revert(string? tunnel, Func<bool>? abortIf)
    {
        Step(() => dns.RestoreSaved(abortIf, tunnel), "dns restore");
        Step(() => routes.RestoreSavedExclusions(abortIf, tunnel), "route exclusion restore");
        Step(() => routes.RestoreSavedLanExclusions(abortIf, tunnel), "lan exclusion restore");
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
            logger.LogWarning(ex, "cleaning up after an earlier session failed at the step '{What}'; leftover routes or DNS settings may remain until the next start", what);
        }
    }
}
