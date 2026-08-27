namespace AmneziaGeo.Windows.App;

/// <summary>
/// Which supervisor drives the machine: the set of tunnels, or the single one. Read once, when the agent starts.
/// </summary>
internal sealed class AgentMode
{
    /// <summary>
    /// Whether the machine keeps several tunnels up.
    /// </summary>
    public bool MultiServer { get; set; }
}
