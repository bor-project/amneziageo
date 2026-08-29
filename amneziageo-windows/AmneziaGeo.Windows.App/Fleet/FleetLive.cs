namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// The tunnels of the set that are up right now, each with its own state. The window reads their readings off
/// these; the set itself says only what is asked for.
/// </summary>
internal sealed class FleetLive
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AgentControl> _up = new(StringComparer.Ordinal);
    private long _turn;

    /// <summary>
    /// Counts the rounds the tunnels were brought in line with the set in.
    /// </summary>
    public long Turn => Interlocked.Read(ref _turn);

    /// <summary>
    /// Marks a round done.
    /// </summary>
    public void Turned()
    {
        Interlocked.Increment(ref _turn);
    }

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
    /// Lists a tunnel under the name its configuration is called now.
    /// </summary>
    public void Retarget(string oldName, string newName)
    {
        lock (_gate)
        {
            if (_up.Remove(oldName, out var control))
            {
                _up[newName] = control;
            }
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
    /// The round trip of every tunnel that has one measured; the balancer picks by these.
    /// </summary>
    public IReadOnlyDictionary<string, int> RoundTrips()
    {
        lock (_gate)
        {
            var readings = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in _up)
            {
                var rtt = pair.Value.Link.RttMs;
                if (rtt >= 0)
                {
                    readings[pair.Key] = rtt;
                }
            }

            return readings;
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
