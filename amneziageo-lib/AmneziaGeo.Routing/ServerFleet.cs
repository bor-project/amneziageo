namespace AmneziaGeo.Routing;

/// <summary>
/// The machine state a rule is resolved against: whether several servers work at once, which configurations
/// exist, and which of them are up right now, priority first. A configuration that is switched off is simply
/// not up.
/// </summary>
public sealed class ServerFleet
{
    private readonly HashSet<string> _known;

    private readonly List<string> _up;

    private readonly HashSet<string> _upIndex;

    /// <summary>
    /// ctor
    /// </summary>
    public ServerFleet(bool multiServer, IEnumerable<string> known, IEnumerable<string> up)
    {
        MultiServer = multiServer;
        _known = new HashSet<string>(known, StringComparer.Ordinal);
        _upIndex = new HashSet<string>(StringComparer.Ordinal);
        _up = [];
        foreach (var server in up)
        {
            if (_known.Contains(server) && _upIndex.Add(server))
            {
                _up.Add(server);
            }
        }
    }

    /// <summary>
    /// The fleet of one: the picked configuration carries everything and no rule addresses a server by name.
    /// </summary>
    public static ServerFleet Single(string config)
    {
        return new ServerFleet(false, [config], [config]);
    }

    /// <summary>
    /// Whether several servers work at once.
    /// </summary>
    public bool MultiServer { get; }

    /// <summary>
    /// Whether any server is up; with none the traffic has no tunnel to take.
    /// </summary>
    public bool AnyUp => _up.Count > 0;

    /// <summary>
    /// The servers up right now, priority top down.
    /// </summary>
    public IReadOnlyList<string> Up => _up;

    /// <summary>
    /// The first server up, priority top down: the one carrying the default route.
    /// </summary>
    public string First => _up.Count > 0 ? _up[0] : string.Empty;

    /// <summary>
    /// The best server up. No measure feeds this yet, so the priority decides.
    /// </summary>
    public string Best => First;

    /// <summary>
    /// Whether a configuration answers to the name.
    /// </summary>
    public bool Knows(string server)
    {
        return _known.Contains(server);
    }

    /// <summary>
    /// Whether the named configuration is up.
    /// </summary>
    public bool IsUp(string server)
    {
        return _upIndex.Contains(server);
    }
}
