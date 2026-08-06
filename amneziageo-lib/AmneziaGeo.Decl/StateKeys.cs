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
    /// Id of the globally selected routing list; empty means a full tunnel.
    /// </summary>
    public const string SelectedRoutingList = "selected-routing-list";
}
