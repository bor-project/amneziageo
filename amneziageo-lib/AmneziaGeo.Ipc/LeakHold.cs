namespace AmneziaGeo.Ipc;

/// <summary>
/// Whether a tunnel is taken down by the user alone: the leak guard of its configuration is on and a session has
/// stood since the user last took the tunnel down. Such a tunnel is kept standing while it carries nothing, so what
/// the rules send through it does not leave directly.
/// </summary>
public sealed class LeakHold
{
    private volatile bool _guard;
    private volatile bool _stood;
    private volatile bool _down;

    /// <summary>
    /// Whether the leak guard of the configuration is on.
    /// </summary>
    public bool Guard => _guard;

    /// <summary>
    /// Whether a session has stood since the user last took the tunnel down.
    /// </summary>
    public bool Stood => _stood;

    /// <summary>
    /// Whether the tunnel is taken down by the user alone.
    /// </summary>
    public bool Active => _guard && _stood;

    /// <summary>
    /// Whether the tunnel stands held while it carries nothing.
    /// </summary>
    public bool Down => _down;

    /// <summary>
    /// Takes the leak guard of the configuration.
    /// </summary>
    public void Set(bool guard)
    {
        _guard = guard;
        if (!guard)
        {
            _down = false;
        }
    }

    /// <summary>
    /// Notes a session being raised anew: nothing stands held any more.
    /// </summary>
    public void Dialled() => _down = false;

    /// <summary>
    /// Notes a session that came up.
    /// </summary>
    public void Raised()
    {
        _stood = true;
        _down = false;
    }

    /// <summary>
    /// Notes a tunnel the user took down.
    /// </summary>
    public void Released()
    {
        _stood = false;
        _down = false;
    }

    /// <summary>
    /// Notes a held tunnel that stopped carrying; true when it was carrying until now.
    /// </summary>
    public bool Stalled()
    {
        if (!Active || _down)
        {
            return false;
        }

        _down = true;
        return true;
    }

    /// <summary>
    /// Notes a held tunnel that carries again; true when it was held down until now.
    /// </summary>
    public bool Carries()
    {
        if (!_down)
        {
            return false;
        }

        _down = false;
        return true;
    }
}
