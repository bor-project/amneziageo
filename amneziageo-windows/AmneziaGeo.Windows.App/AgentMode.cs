namespace AmneziaGeo.Windows.App;

/// <summary>
/// Which supervisor drives the machine: the set of tunnels, or the single one. Read when the agent starts and
/// on every move of the flag.
/// </summary>
internal sealed class AgentMode
{
    /// <summary>
    /// Whether the machine keeps several tunnels up.
    /// </summary>
    public bool MultiServer { get; set; }

    /// <summary>
    /// Whether the flag moved under a running machine. A set remembered from before it moved comes back up
    /// without waiting for 'stay connected after a restart' to allow it.
    /// </summary>
    public bool Switched { get; set; }

    /// <summary>
    /// Whether the machine stood on its single tunnel when the flag last moved. A set that remembers none of
    /// its own stands up on that tunnel, and the single one goes back on it when the flag moves back.
    /// </summary>
    public bool SoleWasUp { get; set; }
}
