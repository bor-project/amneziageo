namespace AmneziaGeo.Windows.App;

/// <summary>
/// Control surface shared between the IPC broker and the profile runner.
/// </summary>
internal sealed class AgentControl
{
    /// <summary>
    /// Store key for the selected target profile.
    /// </summary>
    public const string SelectedTargetKey = "selected-target";

    private readonly Lock _gate = new();
    private volatile bool _running;
    private volatile bool _restartRequired;
    private volatile string? _target;
    private volatile string? _runningTarget;
    private volatile bool _connectFailed;
    private CancellationTokenSource _change = new();

    /// <summary>
    /// Whether the agent keeps a tunnel up.
    /// </summary>
    public bool Running => _running;

    /// <summary>
    /// A connected tunnel must be reconnected to apply a changed setting.
    /// </summary>
    public bool RestartRequired => _restartRequired;

    /// <summary>
    /// The user-selected profile (radio).
    /// </summary>
    public string? Target => _target;

    /// <summary>
    /// The profile the running tunnel is bound to.
    /// </summary>
    public string? RunningTarget => _runningTarget;

    /// <summary>
    /// One-shot flag: the last connect attempt gave up.
    /// </summary>
    public bool ConnectFailed => _connectFailed;

    /// <summary>
    /// Fires when the desired state or persisted configuration changes.
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
    /// Sets the desired connection state and signals the runner.
    /// </summary>
    public void SetRunning(bool value)
    {
        _running = value;
        _connectFailed = false;
        if (value)
        {
            _runningTarget = _target;
        }

        _restartRequired = false;
        Signal();
    }

    /// <summary>
    /// Selects the active target profile without changing running state.
    /// </summary>
    public void SetTarget(string name)
    {
        // No signal: selecting does not switch a live tunnel.
        _target = name;
    }

    /// <summary>
    /// Clears the selected target.
    /// </summary>
    public void ClearTarget()
    {
        _target = null;
    }

    /// <summary>
    /// Follows a profile rename in the live binding without switching the tunnel, so the supervisor keeps
    /// resolving the running profile - a stale target reads as a broken binding on the next re-dial.
    /// </summary>
    public void RetargetName(string oldName, string newName)
    {
        if (string.Equals(_target, oldName, StringComparison.Ordinal))
        {
            _target = newName;
        }

        if (string.Equals(_runningTarget, oldName, StringComparison.Ordinal))
        {
            _runningTarget = newName;
        }
    }

    /// <summary>
    /// Signals that persisted configuration changed and must be re-applied.
    /// </summary>
    public void Invalidate()
    {
        Signal();
    }

    /// <summary>
    /// Latches a failed connect and drops to stopped.
    /// </summary>
    public void FailConnect()
    {
        _connectFailed = true;
        _running = false;
        _runningTarget = null;
        _restartRequired = false;
        Signal();
    }

    /// <summary>
    /// Flags a connected tunnel as needing reconnect.
    /// </summary>
    public void SetRestartRequired()
    {
        _restartRequired = true;
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
