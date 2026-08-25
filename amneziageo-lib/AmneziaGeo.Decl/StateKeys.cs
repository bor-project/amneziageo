namespace AmneziaGeo.Decl;

/// <summary>
/// Setting keys the agent and the store share.
/// </summary>
public static class StateKeys
{
    /// <summary>
    /// Name of the config the agent binds to.
    /// </summary>
    public const string SelectedTarget = "selected-target";

    /// <summary>
    /// Id of the default routing list every unbound config falls back to; empty turns routing off and leaves the
    /// config's own AllowedIPs.
    /// </summary>
    public const string SelectedRoutingList = "selected-routing-list";

    /// <summary>
    /// Marks that the move to per-config routing has stamped the configs that predate it.
    /// </summary>
    public const string ConfigRoutingStamped = "config-routing-stamped";

    /// <summary>
    /// Names of the tunnels the agent keeps up, one per line; read back after a restart.
    /// </summary>
    public const string DesiredTunnels = "desired-tunnels";

    /// <summary>
    /// Names of the tunnels a connect raises when it names none, one per line; a tunnel taken down by name
    /// leaves the set, a disconnect of everything keeps it.
    /// </summary>
    public const string KeptTunnels = "kept-tunnels";

    /// <summary>
    /// Name of the config the user wants to own the default route; empty leaves it to the first tunnel that
    /// carries everything.
    /// </summary>
    public const string DefaultRouteOwner = "default-route-owner";
}
