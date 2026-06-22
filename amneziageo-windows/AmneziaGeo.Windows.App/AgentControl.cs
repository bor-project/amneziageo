namespace AmneziaGeo.Windows.App;

/// <summary>
/// Shared, mutable control surface between the IPC command broker and the running balancer.
/// Carries the desired connection state (running vs stopped) and a change signal the runner
/// observes to re-apply configuration or routing live, without restarting the agent process.
/// </summary>
internal sealed class AgentControl
{
    /// <summary>
    /// Store-settings key under which the user-selected target profile is persisted, so a chosen
    /// profile survives an agent/host restart instead of reverting to a launch-argument default.
    /// </summary>
    public const string SelectedTargetKey = "selected-target";

    private readonly Lock _gate = new();
    private volatile bool _running;
    private volatile bool _restartRequired;
    private volatile string? _betterMember;
    private volatile string? _target;
    private volatile string? _runningTarget;
    private volatile bool _connectFailed;
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
    /// The profile the user has selected (the radio). The next connect binds to it; selecting it does
    /// NOT switch a live tunnel.
    /// </summary>
    public string? Target => _target;

    /// <summary>
    /// The profile the running tunnel is actually bound to — latched from <see cref="Target"/> on each
    /// connect. Differs from <see cref="Target"/> when the user has selected another profile but not yet
    /// reconnected: the connection status reflects this one, the radio reflects <see cref="Target"/>.
    /// </summary>
    public string? RunningTarget => _runningTarget;

    /// <summary>
    /// One-shot flag latched when a user-initiated connect gave up without bringing up any member within
    /// the data-driven deadline. The UI surfaces it as a "failed to connect" banner. Cleared on the next
    /// connect / disconnect command (a fresh user action).
    /// </summary>
    public bool ConnectFailed => _connectFailed;

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
        // A fresh user connect / disconnect clears any prior failed-connect notice.
        _connectFailed = false;
        // Latch the selected target as the running target on connect: the runner brings up the
        // currently-selected profile and then stays on it (live edits re-apply; a later selection does
        // not switch the tunnel until the next connect).
        if (value)
        {
            _runningTarget = _target;
        }

        _restartRequired = false;
        _betterMember = null;
        Signal();
    }

    /// <summary>
    /// Selects the active target profile and signals the runner to switch to it. Does not change the
    /// running / stopped state: switching while connected reconnects to the new target; while stopped it
    /// just becomes the target the next connect uses.
    /// </summary>
    public void SetTarget(string name)
    {
        // No auto-switch: selecting a profile only records the desired target. A connected tunnel keeps
        // running its latched target until the user reconnects (the UI shows a "reconnect to apply"
        // notice); a stopped agent uses this target on the next connect. So we deliberately do NOT
        // signal the runner here.
        _target = name;
    }

    /// <summary>
    /// Clears the selected target (no profile chosen). Used when the bound profile/config is deleted or
    /// the persisted selection is found dangling, so the connection card stops showing a phantom target.
    /// Does not signal: callers decide whether a re-evaluation is needed.
    /// </summary>
    public void ClearTarget()
    {
        _target = null;
    }

    /// <summary>
    /// Signals the runner that persisted configuration or routing changed and must be re-applied.
    /// </summary>
    public void Invalidate()
    {
        Signal();
    }

    /// <summary>
    /// Called by the runner when a connect attempt gave up (no member reachable within the deadline):
    /// latches the one-shot failed notice, drops the desired state to stopped (отбой), and signals the
    /// supervisor to idle.
    /// </summary>
    public void FailConnect()
    {
        _connectFailed = true;
        _running = false;
        _runningTarget = null;
        _restartRequired = false;
        _betterMember = null;
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
