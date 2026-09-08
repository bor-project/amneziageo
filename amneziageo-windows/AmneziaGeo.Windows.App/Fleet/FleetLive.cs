namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// The tunnels of the set that are up right now, each with its own state. The window reads their readings off
/// these; the set itself says only what is asked for.
/// </summary>
internal sealed class FleetLive
{
    // How long a tunnel that lost the link keeps the rules addressed to it: a reconnect is not a move of them.
    private const long HoldMs = 20_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, AgentControl> _up = new(StringComparer.Ordinal);

    // The tunnels that have stood at least once, and when the ones standing no longer lost the link.
    private readonly HashSet<string> _stood = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fell = new(StringComparer.Ordinal);
    private CancellationTokenSource _change = new();
    private long _turn;

    /// <summary>
    /// Fires when a tunnel of the set stands up or gives up.
    /// </summary>
    public CancellationToken ChangeToken
    {
        get
        {
            lock (_gate)
            {
                return _change.Token;
            }
        }
    }

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
    /// Says a tunnel of the set moved between standing and not.
    /// </summary>
    public void Stirred()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            old = _change;
            _change = new CancellationTokenSource();
        }

        old.Cancel();
        old.Dispose();
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
            _stood.Remove(name);
            _fell.Remove(name);
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

            if (_stood.Remove(oldName))
            {
                _stood.Add(newName);
            }

            if (_fell.Remove(oldName, out var since))
            {
                _fell[newName] = since;
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
            _stood.Clear();
            _fell.Clear();
        }
    }

    /// <summary>
    /// The tunnels of the set carrying nothing addressed to them: raised but not standing, and past the wait a
    /// reconnect is given. One the list does not name yet is still on its way up, so it keeps its own rules.
    /// </summary>
    public IReadOnlySet<string> Fallen()
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            var fallen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in _up)
            {
                if (pair.Value.ConnectFailed)
                {
                    _fell.Remove(pair.Key);
                    fallen.Add(pair.Key);
                    continue;
                }

                if (pair.Value.Connected)
                {
                    _stood.Add(pair.Key);
                    _fell.Remove(pair.Key);
                    continue;
                }

                if (!_stood.Contains(pair.Key))
                {
                    _fell.Remove(pair.Key);
                    fallen.Add(pair.Key);
                    continue;
                }

                if (!_fell.TryGetValue(pair.Key, out var since))
                {
                    since = now;
                    _fell[pair.Key] = since;
                }

                if (now - since >= HoldMs)
                {
                    fallen.Add(pair.Key);
                }
            }

            return fallen;
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
