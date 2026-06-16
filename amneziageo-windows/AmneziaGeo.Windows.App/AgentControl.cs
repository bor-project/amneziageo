namespace AmneziaGeo.Windows.App;

/// <summary>
/// Shared, mutable control surface between the IPC command broker and the running balancer.
/// Carries the desired connection state (running vs stopped) and a change signal the runner
/// observes to re-apply configuration or routing live, without restarting the agent process.
/// </summary>
internal sealed class AgentControl
{
    private readonly Lock _gate = new();
    private volatile bool _running;
    private volatile bool _restartRequired;
    private volatile string? _betterMember;
    private CancellationTokenSource _change = new();

    /// <summary>
    /// Whether the agent should keep a tunnel up. Toggled by the connect / disconnect command.
    /// Starts <c>false</c>: the agent comes up disconnected and the user initiates the connection.
    /// </summary>
    public bool Running => _running;

    /// <summary>
    /// Set when a setting that only applies on a fresh tunnel (e.g. the routing toggle) changed while
    /// the bound target is connected. The UI surfaces this as a "reconnect to apply" notice. We do not
    /// re-apply such changes in place — that left a half-applied split/full state — so the user
    /// reconnects to apply. Cleared on any connect / disconnect.
    /// </summary>
    public bool RestartRequired => _restartRequired;

    /// <summary>
    /// The name of a higher-priority (or lower-latency) member that is consistently available while the
    /// balancer is running on a backup. Notify-only: the balancer does not switch back automatically
    /// (silently dropping a working backup is disruptive); the UI offers the user a return. Null when
    /// nothing better is available. Cleared on any connect / disconnect.
    /// </summary>
    public string? BetterMember => _betterMember;

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
    /// Sets the desired connection state and signals the runner to re-evaluate. Clears the
    /// restart-required and better-member notices: a connect applies the current settings, and a
    /// disconnect makes both notices moot.
    /// </summary>
    public void SetRunning(bool value)
    {
        _running = value;
        _restartRequired = false;
        _betterMember = null;
        Signal();
    }

    /// <summary>
    /// Signals the runner that persisted configuration or routing changed and must be re-applied.
    /// </summary>
    public void Invalidate()
    {
        Signal();
    }

    /// <summary>
    /// Flags that a connected tunnel must be reconnected for a just-changed setting to take effect.
    /// </summary>
    public void SetRestartRequired()
    {
        _restartRequired = true;
    }

    /// <summary>
    /// Records (or clears, with null) the better member the balancer found while on a backup.
    /// </summary>
    public void SetBetterMember(string? member)
    {
        _betterMember = member;
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
