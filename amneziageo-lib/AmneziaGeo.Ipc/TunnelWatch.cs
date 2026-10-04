namespace AmneziaGeo.Ipc;

/// <summary>
/// What the head does about a session whose tunnel process is gone.
/// </summary>
public enum TunnelGoneStep
{
    /// <summary>
    /// Nobody wants the session any more: it shows as down.
    /// </summary>
    Down,

    /// <summary>
    /// The session is raised again.
    /// </summary>
    Raise,

    /// <summary>
    /// The session shows as down and is raised once the window is on the screen.
    /// </summary>
    Owed,

    /// <summary>
    /// The session went with its process too often: it shows as failed.
    /// </summary>
    GiveUp,
}

/// <summary>
/// Follows the tunnel process from the head and decides what becomes of a session that went with it.
/// </summary>
public sealed class TunnelWatch
{
    private readonly object _gate = new();
    private readonly Queue<long> _raises = new();
    private readonly int _limit;
    private readonly long _windowMs;
    private bool _seen;
    private bool _owed;

    /// <summary>
    /// ctor
    /// </summary>
    public TunnelWatch(int limit, long windowMs)
    {
        _limit = limit;
        _windowMs = windowMs;
    }

    /// <summary>
    /// Takes a connect that was asked for; its process may be yet to come.
    /// </summary>
    public void Asked(bool running)
    {
        lock (_gate)
        {
            _seen = running;
            _owed = false;
        }
    }

    /// <summary>
    /// Takes a word of the tunnel: its process is there.
    /// </summary>
    public void Heard()
    {
        lock (_gate)
        {
            _seen = true;
        }
    }

    /// <summary>
    /// Takes a disconnect that was asked for.
    /// </summary>
    public void Dropped()
    {
        lock (_gate)
        {
            _seen = false;
            _owed = false;
        }
    }

    /// <summary>
    /// Takes a look at the tunnel process; a step once a session that was seen alive has no process, null otherwise.
    /// </summary>
    public TunnelGoneStep? Look(bool active, bool running, bool wanted, bool shown, long now)
    {
        lock (_gate)
        {
            if (!active)
            {
                return null;
            }

            if (running)
            {
                _seen = true;
                return null;
            }

            if (!_seen)
            {
                return null;
            }

            _seen = false;
            if (!wanted)
            {
                _owed = false;
                return TunnelGoneStep.Down;
            }

            if (!shown)
            {
                _owed = true;
                return TunnelGoneStep.Owed;
            }

            return Take(now) ? TunnelGoneStep.Raise : TunnelGoneStep.GiveUp;
        }
    }

    /// <summary>
    /// Whether the window that came to the screen raises the session it is owed.
    /// </summary>
    public bool Due(bool active, bool wanted, long now)
    {
        lock (_gate)
        {
            if (!_owed || active)
            {
                return false;
            }

            _owed = false;
            return wanted && Take(now);
        }
    }

    // Counts a raise unless the window of time already holds as many as allowed.
    private bool Take(long now)
    {
        while (_raises.Count > 0 && now - _raises.Peek() >= _windowMs)
        {
            _raises.Dequeue();
        }

        if (_raises.Count >= _limit)
        {
            return false;
        }

        _raises.Enqueue(now);
        return true;
    }
}
