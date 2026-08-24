using AmneziaGeo.Ipc;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Desired state and live readings of a single tunnel. One of these exists per configuration the agent has been
/// asked to raise, so tunnels no longer share a slot.
/// </summary>
internal sealed class TunnelControl
{
    private static long _sequence;

    private readonly Lock _gate = new();
    private readonly Action _signalStatus;
    private readonly Action _signalMembership;
    private volatile string _config;
    private volatile string _ownerRoot;
    private volatile string? _ownerSid;
    private volatile bool _running;
    private volatile bool _restartRequired;
    private volatile bool _connectFailed;
    private volatile ConnectFailureReason _connectFailReason;
    private volatile string? _connectFailDetail;
    private volatile bool _disconnectFailed;
    private volatile string? _disconnectFailDetail;
    private volatile int _retryAttempt;
    private volatile int _handshakeAge = -1;
    private volatile LinkReading _link = LinkReading.Empty;
    private volatile bool _awaitingRetry;
    private volatile bool _dnsUnreachable;
    private volatile bool _wakePending;
    private CancellationTokenSource _change = new();
    private CancellationTokenSource _wake = new();

    /// <summary>
    /// ctor
    /// </summary>
    public TunnelControl(string config, string ownerRoot, string? ownerSid, Action signalStatus, Action signalMembership)
    {
        _config = config;
        _ownerRoot = ownerRoot;
        _ownerSid = ownerSid;
        _signalStatus = signalStatus;
        _signalMembership = signalMembership;
    }

    /// <summary>
    /// Order this tunnel was raised in; the oldest one is the one machine-wide readings speak for.
    /// </summary>
    public long Sequence { get; } = Interlocked.Increment(ref _sequence);

    /// <summary>
    /// The configuration this tunnel runs.
    /// </summary>
    public string Config => _config;

    /// <summary>
    /// The data root of the user who raised this tunnel.
    /// </summary>
    public string OwnerRoot => _ownerRoot;

    /// <summary>
    /// The SID of the user who raised this tunnel, or null when unknown.
    /// </summary>
    public string? OwnerSid => _ownerSid;

    /// <summary>
    /// Whether the agent keeps this tunnel up.
    /// </summary>
    public bool Running => _running;

    /// <summary>
    /// How long ago the peer last answered, in reporting steps; -1 before it ever has.
    /// </summary>
    public int HandshakeAge => _handshakeAge;

    /// <summary>
    /// The tunnel's throughput and handshake rate.
    /// </summary>
    public LinkReading Link => _link;

    /// <summary>
    /// Whether the resolver this machine sends its lookups to stopped answering while this tunnel is up.
    /// </summary>
    public bool DnsUnreachable => _dnsUnreachable;

    /// <summary>
    /// A connected tunnel must be reconnected to apply a changed setting.
    /// </summary>
    public bool RestartRequired => _restartRequired;

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
    /// Fires when this tunnel's desired state or persisted configuration changes.
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
    /// Returns whether the given user raised this tunnel (by SID when both are known, else by data root).
    /// </summary>
    public bool IsOwnedBy(string root, string? sid)
    {
        if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(_ownerSid))
        {
            return string.Equals(_ownerSid, sid, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_ownerRoot)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Binds the tunnel to a user's data root and SID.
    /// </summary>
    public void SetOwner(string root, string? sid)
    {
        _ownerRoot = root;
        _ownerSid = sid;
    }

    /// <summary>
    /// Records the handshake age and reports whether the step moved.
    /// </summary>
    public bool SetHandshakeAge(int seconds)
    {
        var moved = _handshakeAge != seconds;
        _handshakeAge = seconds;
        return moved;
    }

    /// <summary>
    /// Records the link reading and reports whether it moved enough to show.
    /// </summary>
    public bool SetLink(LinkReading reading)
    {
        var previous = _link;
        _link = reading;
        return reading.DiffersFrom(previous);
    }

    /// <summary>
    /// Records the resolver verdict and reports whether it moved.
    /// </summary>
    public bool SetDnsUnreachable(bool unreachable)
    {
        var moved = _dnsUnreachable != unreachable;
        _dnsUnreachable = unreachable;
        return moved;
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
        _wakePending = false;
        _restartRequired = false;
        Signal();
        _signalMembership();
        _signalStatus();
    }

    /// <summary>
    /// Follows a config rename without switching the tunnel, so the supervisor keeps resolving the running
    /// config - a stale name reads as a broken binding on the next re-dial.
    /// </summary>
    public void Rename(string newName)
    {
        _config = newName;
    }

    /// <summary>
    /// Wakes status waiters after a reading or a state change.
    /// </summary>
    public void SignalStatus()
    {
        _signalStatus();
    }

    /// <summary>
    /// Signals that persisted configuration changed and must be re-applied.
    /// </summary>
    public void Invalidate()
    {
        Signal();
        _signalStatus();
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
        _restartRequired = false;
        _retryAttempt = 0;
        Signal();
        _signalMembership();
        _signalStatus();
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
        _wakePending = false;
    }

    /// <summary>
    /// Opens a retry backoff and returns the token that ends the wait early.
    /// </summary>
    public CancellationToken BeginRetryWait()
    {
        lock (_gate)
        {
            _wake = new CancellationTokenSource();
            _awaitingRetry = true;
            if (_wakePending)
            {
                _wakePending = false;
                _wake.Cancel();
            }

            return _wake.Token;
        }
    }

    /// <summary>
    /// Closes the retry backoff.
    /// </summary>
    public void EndRetryWait()
    {
        _awaitingRetry = false;
    }

    /// <summary>
    /// Ends the backoff wait early on a network change. Deliberately not a change signal: a wake shortens the
    /// wait without aborting an in-flight connect attempt, re-entering the supervisor or tearing down a tunnel.
    /// </summary>
    public void WakeIfRetrying()
    {
        var wake = TakeWake();
        wake?.Cancel();
    }

    /// <summary>
    /// Drops the live readings once the tunnel is down.
    /// </summary>
    public void ClearReadings()
    {
        _handshakeAge = -1;
        _link = LinkReading.Empty;
        _dnsUnreachable = false;
    }

    // Returns the open backoff, or null after latching the change for the next one - a network that recovers
    // mid-attempt must still shorten the backoff that follows the failure.
    private CancellationTokenSource? TakeWake()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return null;
            }

            if (!_awaitingRetry)
            {
                _wakePending = true;
                return null;
            }

            return _wake;
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
