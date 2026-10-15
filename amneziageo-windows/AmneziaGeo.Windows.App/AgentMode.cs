namespace AmneziaGeo.Windows.App;

/// <summary>
/// Which supervisor drives the machine: the set of tunnels, or the single one. Read when the agent starts and
/// on every move of the flag.
/// </summary>
internal sealed class AgentMode
{
    private static readonly TimeSpan SettleStep = TimeSpan.FromMilliseconds(100);

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _wakeLock = new();
    private volatile bool _multiServer;
    private volatile bool _standing;

    /// <summary>
    /// Whether the machine keeps several tunnels up.
    /// </summary>
    public bool MultiServer
    {
        get => _multiServer;
        set => _multiServer = value;
    }

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

    /// <summary>
    /// Marks the supervisor of the mode as up and taking requests.
    /// </summary>
    public void Stood()
    {
        _standing = true;
    }

    /// <summary>
    /// Marks the machine as changing over to the other supervisor.
    /// </summary>
    public void Leaving()
    {
        _standing = false;
    }

    /// <summary>
    /// Asks for the flag to be read now.
    /// </summary>
    public void Wake()
    {
        lock (_wakeLock)
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
    }

    /// <summary>
    /// Waits out the period between reads of the flag, or less when woken.
    /// </summary>
    public async Task NapAsync(TimeSpan period, CancellationToken ct)
    {
        await _wake.WaitAsync(period, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Wakes the switch and waits until the supervisor of the wanted mode stands; false when the budget runs out
    /// first.
    /// </summary>
    public async Task<bool> SettleAsync(bool wanted, TimeSpan budget, CancellationToken ct)
    {
        Wake();
        var until = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        while (_multiServer != wanted || !_standing)
        {
            if (Environment.TickCount64 >= until)
            {
                return false;
            }

            await Task.Delay(SettleStep, ct).ConfigureAwait(false);
        }

        return true;
    }
}
