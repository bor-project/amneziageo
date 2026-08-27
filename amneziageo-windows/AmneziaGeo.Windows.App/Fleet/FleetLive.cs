namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// The tunnels of the set that are up right now, each with its own state. The window reads their readings off
/// these; the set itself says only what is asked for.
/// </summary>
internal sealed class FleetLive
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AgentControl> _up = new(StringComparer.Ordinal);

    /// <summary>
    /// Puts a raised tunnel on the list.
    /// </summary>
    public void Publish(string name, AgentControl control)
    {
        lock (_gate)
        {
            _up[name] = control;
        }
    }

    /// <summary>
    /// Takes a tunnel off the list.
    /// </summary>
    public void Drop(string name)
    {
        lock (_gate)
        {
            _up.Remove(name);
        }
    }

    /// <summary>
    /// Empties the list.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _up.Clear();
        }
    }

    /// <summary>
    /// The state of one tunnel of the set, or null while it is not up.
    /// </summary>
    public AgentControl? Of(string name)
    {
        lock (_gate)
        {
            return _up.GetValueOrDefault(name);
        }
    }
}
