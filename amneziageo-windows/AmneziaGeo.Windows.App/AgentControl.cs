using AmneziaGeo.Ipc;

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
    private volatile ConnectFailureReason _connectFailReason;
    private volatile string? _connectFailDetail;
    private volatile bool _disconnectFailed;
    private volatile string? _disconnectFailDetail;
    private volatile int _retryAttempt;
    private volatile bool _awaitingRetry;
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
    /// Structured reason for the last failed connect.
    /// </summary>
    public ConnectFailureReason ConnectFailReason => _connectFailReason;

    /// <summary>
    /// Short cause label for the last failed connect.
    /// </summary>
    public string? ConnectFailDetail => _connectFailDetail;

    /// <summary>
    /// One-shot flag: the last disconnect left the tunnel service running.
    /// </summary>
    public bool DisconnectFailed => _disconnectFailed;

    /// <summary>
    /// Short cause label for the last failed disconnect (service state).
    /// </summary>
    public string? DisconnectFailDetail => _disconnectFailDetail;

    /// <summary>
    /// Transient-failure retry count for the current dial; 0 when not retrying.
    /// </summary>
    public int RetryAttempt => _retryAttempt;

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
        _connectFailReason = ConnectFailureReason.Unknown;
        _connectFailDetail = null;
        _disconnectFailed = false;
        _disconnectFailDetail = null;
        _retryAttempt = 0;
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
    /// Latches a failed connect with its classified reason and drops to stopped.
    /// </summary>
    public void FailConnect(ConnectFailureReason reason, string? detail)
    {
        _connectFailReason = reason;
        _connectFailDetail = detail;
        _connectFailed = true;
        _running = false;
        _runningTarget = null;
        _restartRequired = false;
        _retryAttempt = 0;
        Signal();
    }

    /// <summary>
    /// Latches a failed disconnect: the teardown left the tunnel service running, so the connected state is
    /// kept and the user can retry.
    /// </summary>
    public void FailDisconnect(string? detail)
    {
        // A concurrent connect (SetRunning(true)) supersedes the disconnect, so don't latch a failure the user
        // no longer wants - it would otherwise stick through the whole healthy session.
        if (_running)
        {
            return;
        }

        _disconnectFailDetail = detail;
        _disconnectFailed = true;
    }

    /// <summary>
    /// Clears a latched disconnect failure after a clean teardown.
    /// </summary>
    public void ClearDisconnectFail()
    {
        _disconnectFailed = false;
        _disconnectFailDetail = null;
    }

    /// <summary>
    /// Flags a connected tunnel as needing reconnect.
    /// </summary>
    public void SetRestartRequired()
    {
        _restartRequired = true;
    }

    /// <summary>
    /// Advances the transient-failure retry count and returns the new value.
    /// </summary>
    public int NextRetry()
    {
        lock (_gate)
        {
            return ++_retryAttempt;
        }
    }

    /// <summary>
    /// Clears the retry count after a successful connect.
    /// </summary>
    public void ClearRetry()
    {
        _retryAttempt = 0;
        _awaitingRetry = false;
    }

    /// <summary>
    /// Marks whether the runner is waiting out a retry backoff (vs mid-attempt).
    /// </summary>
    public void SetAwaitingRetry(bool value)
    {
        _awaitingRetry = value;
    }

    /// <summary>
    /// Signals a re-dial only while waiting out a backoff, so a network change shortens the wait without
    /// aborting an in-flight connect attempt or tearing down a live tunnel.
    /// </summary>
    public void WakeIfRetrying()
    {
        if (_running && _awaitingRetry)
        {
            Signal();
        }
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
