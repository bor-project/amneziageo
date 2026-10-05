namespace AmneziaGeo.Ipc;

/// <summary>
/// Decides at which looks at the link the networks of the device are read again.
/// </summary>
public sealed class NetworkLooks
{
    /// <summary>
    /// Looks after which the networks are read whatever the system told.
    /// </summary>
    public const int Every = 12;

    private readonly Dictionary<(long Network, bool Link), int> _marks = [];
    private int _changed = 1;
    private int _quiet;

    /// <summary>
    /// Notes that the system told of a change in a network.
    /// </summary>
    public void Changed() => Volatile.Write(ref _changed, 1);

    /// <summary>
    /// Notes what a read takes from the abilities or from the link of a network, a change when it differs from the last.
    /// </summary>
    public void Noted(long network, bool link, int mark)
    {
        lock (_marks)
        {
            if (_marks.TryGetValue((network, link), out var known) && known == mark)
            {
                return;
            }

            _marks[(network, link)] = mark;
        }

        Changed();
    }

    /// <summary>
    /// Forgets a network that is gone, which is a change.
    /// </summary>
    public void Gone(long network)
    {
        lock (_marks)
        {
            _marks.Remove((network, false));
            _marks.Remove((network, true));
        }

        Changed();
    }

    /// <summary>
    /// Whether this look reads the networks: after a change, while the caller waits for one, and at every twelfth look.
    /// </summary>
    public bool Due(bool waiting)
    {
        var changed = Interlocked.Exchange(ref _changed, 0) == 1;
        if (!changed && !waiting && ++_quiet < Every)
        {
            return false;
        }

        _quiet = 0;
        return true;
    }
}
