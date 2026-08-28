namespace AmneziaGeo.Windows.App;

/// <summary>
/// The tunnel this machine sends its name lookups through. A machine running one tunnel sends them through it.
/// </summary>
internal class ResolverHolder(AgentControl control)
{
    /// <summary>
    /// The state of the tunnel holding the lookups, or null while none does.
    /// </summary>
    public virtual AgentControl? Current => control.Running ? control : null;
}
