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
    /// Id of the globally selected routing list; empty turns routing off and leaves the config's own AllowedIPs.
    /// </summary>
    public const string SelectedRoutingList = "selected-routing-list";
}
