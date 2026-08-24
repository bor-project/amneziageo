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
}
