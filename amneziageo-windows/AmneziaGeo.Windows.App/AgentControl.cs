namespace AmneziaGeo.Windows.App;

/// <summary>
/// Shared, mutable control surface between the IPC command broker and the running balancer.
/// Carries the desired connection state (running vs stopped) and a change signal the runner
/// observes to re-apply configuration or routing live, without restarting the agent process.
/// </summary>
internal sealed class AgentControl
{
    private readonly Lock _gate = new();
    private volatile bool _running = true;
    private CancellationTokenSource _change = new();

    /// <summary>
    /// Whether the agent should keep a tunnel up. Toggled by the connect / disconnect command.
    /// </summary>
    public bool Running => _running;

    /// <summary>
    /// A token that fires once when the desired state or persisted configuration changes.
    /// Capture it before reading <see cref="Running"/>, then link it into the session's
    /// cancellation so in-flight waits abort promptly and the supervisor re-evaluates.
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
    /// Sets the desired connection state and signals the runner to re-evaluate.
    /// </summary>
    public void SetRunning(bool value)
    {
        _running = value;
        Signal();
    }

    /// <summary>
    /// Signals the runner that persisted configuration or routing changed and must be re-applied.
    /// </summary>
    public void Invalidate()
    {
        Signal();
    }

    private void Signal()
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
}
