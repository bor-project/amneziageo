using Avalonia.Media;
using Avalonia.Threading;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Home screen: the connection card (power control, status, active-config picker), the tray-icon colour, and
/// the top-center notice banner. The config catalogue lives on the shell, reached through <c>_host</c>.
/// </summary>
internal sealed partial class ConnectionViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _host;
    private readonly IAgentConnection _connection;
    private readonly UiPreferences _prefs;
    private readonly DispatcherTimer _noticeTimer;

    private bool _toggleInFlight;
    private string? _lastNotice;
    private bool _suppressActivePush;
    private bool _suppressActiveChoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    [NotifyPropertyChangedFor(nameof(IsConnectingOut))]
    [NotifyPropertyChangedFor(nameof(IsConnectingIn))]
    [NotifyPropertyChangedFor(nameof(ConnectHint))]
    [NotifyPropertyChangedFor(nameof(ShowSelectConfigHint))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleBorderBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleForeground))]
    [NotifyPropertyChangedFor(nameof(ConnectStatusBrush))]
    [NotifyPropertyChangedFor(nameof(TrayStatusColor))]
    [NotifyPropertyChangedFor(nameof(ConnectPillContent))]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    private bool _isTunnelActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    private string? _boundTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    [NotifyPropertyChangedFor(nameof(IsConnectingOut))]
    [NotifyPropertyChangedFor(nameof(IsConnectingIn))]
    [NotifyPropertyChangedFor(nameof(ConnectHint))]
    [NotifyPropertyChangedFor(nameof(ShowSelectConfigHint))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleBorderBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleForeground))]
    [NotifyPropertyChangedFor(nameof(ConnectStatusBrush))]
    [NotifyPropertyChangedFor(nameof(TrayStatusColor))]
    private string _boundStatus = ConnectionStatus.Disconnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    private ConfigItemViewModel? _ActiveConfig;

    [ObservableProperty]
    private ConfigChoice? _ActiveConfigChoice = ConfigChoice.None;

    // False until the first snapshot lands, so the card shows a loader instead of the indeterminate button.
    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private bool _noticeVisible;

    [ObservableProperty]
    private string? _noticeText;

    [ObservableProperty]
    private bool _reconnectAvailable;

    // Settings changed on the live tunnel: the editable sections offer the reconnect in their footer, the rest
    // of the app in the notice banner.
    [ObservableProperty]
    private bool _restartPending;

    // The last dial gave up; keeps a failed trace on the status surfaces until the next connect.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    private bool _connectFailed;

    // The last disconnect stalled with the tunnel still up; keeps the retry banner up until the next command.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoticeActionText))]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    [NotifyPropertyChangedFor(nameof(CanDismissNotice))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    private bool _disconnectFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    private bool _reconnecting;

    // A connect request was refused because another account owns the tunnel; the banner offers a takeover.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoticeActionText))]
    private bool _takeoverPending;

    // Transient-failure retry count reported by the agent; 0 when not retrying.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRetry))]
    [NotifyPropertyChangedFor(nameof(RetryText))]
    [NotifyPropertyChangedFor(nameof(ConnectHint))]
    private int _retryAttempt;

    /// <summary>
    /// ctor
    /// </summary>
    public ConnectionViewModel(MainWindowViewModel host, IAgentConnection connection, UiPreferences prefs)
    {
        _host = host;
        _connection = connection;
        _prefs = prefs;
        Loc.Instance.CultureChanged += OnCultureChanged;
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            NoticeVisible = false;
        };
    }

    /// <summary>
    /// Banner status text in the connection card.
    /// </summary>
    public string AgentStatusText => IsConnected
        ? ConnState switch
        {
            2 => StatusLabels.Text(BoundStatus),
            1 => StatusLabels.Text(IsTunnelActive ? ConnectionStatus.Connecting : ConnectionStatus.Disconnecting),
            _ => StatusLabels.Text(ConnectFailed ? ConnectionStatus.Failed : ConnectionStatus.Disconnected),
        }
        : Loc.Instance.Get("MainVm_NoAgentConnection");

    // Disabled in the stalled-disconnect half-state (tunnel still up but Active=false): the header power toggle
    // would otherwise send connect and reverse the disconnect - the retry is offered on the banner instead (#14).
    public bool CanToggleConnection => !Reconnecting && !DisconnectFailed && IsConnected && (IsTunnelActive || ActiveConfig is not null);

    private static readonly IBrush _circleBlue = new SolidColorBrush(Color.FromRgb(0x2A, 0x6F, 0xDB));
    private static readonly IBrush _circleBorderGray = new SolidColorBrush(Color.FromRgb(0xD9, 0xDD, 0xE6));
    private static readonly IBrush _glyphGray = new SolidColorBrush(Color.FromRgb(0x7B, 0x81, 0x8D));
    private static readonly IBrush _textBlue = new SolidColorBrush(Color.FromRgb(0x1A, 0x50, 0xB0));
    private static readonly IBrush _textGray = new SolidColorBrush(Color.FromRgb(0x5B, 0x61, 0x6E));
    private static readonly IBrush _orange = new SolidColorBrush(Color.FromRgb(0xE0, 0x90, 0x2F));
    private static readonly IBrush _hintBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAB));

    // 0 = disconnected, 1 = transitioning, 2 = connected.
    private int ConnState => BoundStatus switch
    {
        ConnectionStatus.Connected => IsTunnelActive ? 2 : 1,
        ConnectionStatus.Connecting or ConnectionStatus.Disconnecting => 1,
        _ => IsTunnelActive ? 1 : 0,
    };

    public bool IsConnecting => ConnState == 1;

    public bool IsConnectingOut => IsConnecting && IsTunnelActive;

    public bool IsConnectingIn => IsConnecting && !IsTunnelActive;

    public string ConnectHint => ConnState switch
    {
        1 => Loc.Instance.Get(ShowRetry ? "MainVm_ConnectHintRetrying" : "MainVm_ConnectHintConnecting"),
        2 => Loc.Instance.Get("MainVm_ConnectHintClickToDisconnect"),
        _ when ActiveConfig is null => Loc.Instance.Get("MainVm_ConnectHintSelectConfig"),
        _ => Loc.Instance.Get("MainVm_ConnectHintClickToConnect"),
    };

    // A stalled connect is retrying (more than one attempt made).
    public bool ShowRetry => RetryAttempt >= 1;

    // "Attempt N" label; N counts the attempt now in flight (retries past the first).
    public string RetryText => ShowRetry ? Loc.Instance.Get("MainVm_ConnectAttempt", RetryAttempt + 1) : string.Empty;

    public bool ShowSelectConfigHint => ConnState == 0 && _host.HasConfigs && ActiveConfig is null;

    public string ConnectPillContent => IsTunnelActive ? Loc.Instance.Get("MainVm_Disconnect") : Loc.Instance.Get("MainVm_Connect");

    // The notice banner's action label: retry a stalled disconnect (#14), else reconnect / retry a failed connect.
    public string NoticeActionText => TakeoverPending
        ? Loc.Instance.Get("Main_TakeoverButton")
        : DisconnectFailed
            ? Loc.Instance.Get("Main_RetryDisconnectButton")
            : Loc.Instance.Get("Main_ReconnectButton");

    // The stalled-disconnect banner has no dismiss affordance: its toggle is disabled, so dismissing would strand
    // the user with no in-window retry. It clears on its own once the disconnect completes (#14).
    public bool CanDismissNotice => !DisconnectFailed;

    // Colour per state: disconnected grey, transitioning (connect / disconnect) orange, connected blue.
    public IBrush ConnectCircleBrush => ConnState == 2 ? _circleBlue : Brushes.White;

    public IBrush ConnectCircleBorderBrush => ConnState switch
    {
        2 => Brushes.Transparent,
        1 => _orange,
        _ => _circleBorderGray,
    };

    public IBrush ConnectCircleForeground => ConnState switch
    {
        2 => Brushes.White,
        1 => _orange,
        _ => _glyphGray,
    };

    public IBrush ConnectStatusBrush => ConnState switch
    {
        2 => _textBlue,
        1 => _orange,
        _ => _textGray,
    };

    public IBrush ConnectHintBrush => _hintBrush;

    public Color TrayStatusColor => ConnState switch
    {
        2 => Color.FromRgb(0x2A, 0x6F, 0xDB),
        1 => Color.FromRgb(0xE0, 0x90, 0x2F),
        _ => Color.FromRgb(0x7B, 0x81, 0x8D),
    };

    private void OnCultureChanged()
    {
        // Re-raise the localized connection labels on a language change.
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// Marks the agent connection live (its first snapshot follows).
    /// </summary>
    public void SetConnected()
    {
        IsConnected = true;
    }

    /// <summary>
    /// Tears down the connection card on disconnect: clears the live state, the active config, and the notice.
    /// </summary>
    public void Reset()
    {
        IsConnected = false;
        BoundStatus = ConnectionStatus.Disconnected;
        // The catalogue rows were dropped by Config.Reset, so the combo re-mirrors to «— не выбрано —» and
        // connect re-gates until the next reconnect snapshot.
        ActiveConfig = null;
        BoundTarget = null;
        _noticeTimer.Stop();
        _lastNotice = null;
        NoticeVisible = false;
        NoticeText = null;
        ReconnectAvailable = false;
        RestartPending = false;
        ConnectFailed = false;
        DisconnectFailed = false;
        TakeoverPending = false;
    }

    /// <summary>
    /// Applies the connection state, active-config matching, and top-center notice from the snapshot. Runs
    /// after the config catalogue is reconciled, so the matching reads the fresh rows.
    /// </summary>
    public void Apply(StatusSnapshot snapshot)
    {
        // First snapshot: the card leaves the loading state for the real connection UI.
        IsReady = true;
        BoundTarget = snapshot.BoundTarget;
        BoundStatus = snapshot.BoundStatus;
        RetryAttempt = snapshot.RetryAttempt;
        RestartPending = snapshot.RestartRequired;
        if (!_toggleInFlight)
        {
            IsTunnelActive = snapshot.Active;
        }

        var selected = snapshot.SelectedTarget ?? snapshot.BoundTarget;

        // Mirror the agent's selected target into the connection-card config combo without echoing a select
        // back. Prefer the agent's active/selected target; fall back to the last config the user had
        // chosen (restored from prefs) so the window opens on it with connect still gated until present.
        _suppressActivePush = true;
        var active = _host.Config.Configs.FirstOrDefault(b => string.Equals(b.Name, selected, StringComparison.Ordinal));
        if (active is null && !string.IsNullOrEmpty(_prefs.LastConfig))
        {
            active = _host.Config.Configs.FirstOrDefault(b => string.Equals(b.Name, _prefs.LastConfig, StringComparison.Ordinal));
        }

        // The selected config lost its row (deleted here or elsewhere).
        var selectionLost = active is null && ActiveConfig is not null && !_host.Config.Configs.Contains(ActiveConfig);
        if (active is not null)
        {
            ActiveConfig = active;
        }
        else if (selectionLost)
        {
            ActiveConfig = null;
        }
        _suppressActivePush = false;

        // Keep a config selected by default (all platforms): the sole config becomes the default right after
        // the first import, and deleting the selected config hands selection to the next remaining one. Set
        // outside the echo-suppression so it persists like a manual pick.
        if (ActiveConfig is null)
        {
            var fallback = selectionLost
                ? _host.Config.Configs.FirstOrDefault()
                : _host.Config.Configs.Count == 1 ? _host.Config.Configs[0] : null;
            if (fallback is not null)
            {
                ActiveConfig = fallback;
            }
        }

        // A rename moves the selection under the UI, and the assignment above is echo-suppressed, so the
        // preference that restores it on the next start would keep the old name and open with none selected.
        if (ActiveConfig is { } current && !string.Equals(_prefs.LastConfig, current.Name, StringComparison.Ordinal))
        {
            _prefs.LastConfig = current.Name;
            _prefs.Save();
        }

        // Owning the tunnel clears a pending takeover prompt; while it stands, keep it across snapshots.
        if (snapshot.Active)
        {
            TakeoverPending = false;
        }

        if (TakeoverPending)
        {
            return;
        }

        // Top-center notice (auto-hides after 5s, dismissable): a different config is selected while a
        // tunnel is up (reconnect to apply - no auto-switch), settings changed on a live tunnel, or a
        // connect failure. Shown once per distinct notice, not re-armed while the same one holds.
        string? notice = null;
        var reconnect = false;
        if (snapshot.ConnectFailed)
        {
            notice = ConnectFailureNotice(snapshot);
            // Offer a retry from the banner: the failed dial left the tunnel down, so reconnect just re-dials (#11).
            reconnect = true;
        }
        else if (snapshot.DisconnectFailed)
        {
            // The teardown stalled with the tunnel still up: keep a banner with a retry-disconnect action (#14).
            notice = Loc.Instance.Get("MainVm_NoticeDisconnectFailed");
            reconnect = true;
        }
        else if (snapshot.Active && SelectedDiffersFromBound(snapshot))
        {
            // A different config is selected on the live tunnel: reuse the reconnect banner so its action applies
            // the switch (Reconnect dials the newly selected ActiveConfig), like the settings-changed case below.
            notice = Loc.Instance.Get("MainVm_NoticeConfigSelected", snapshot.SelectedTarget);
            reconnect = true;
        }
        else if (snapshot.RestartRequired && !_host.ReconnectPromptInSection)
        {
            // Settings changed on the live tunnel: bound == selected, so reconnecting the active config applies them.
            // An editable section carries the same offer in its footer, so the banner stays out of its way.
            notice = Loc.Instance.Get("MainVm_NoticeSettingsChanged");
            reconnect = true;
        }

        ConnectFailed = snapshot.ConnectFailed;
        DisconnectFailed = snapshot.DisconnectFailed;
        ReconnectAvailable = reconnect;
        // A failed dial (or a stalled disconnect) keeps its banner up until the next command, like the reconnect
        // banner, so a boot auto-connect failure with no window is not lost once the tray balloon fades.
        ShowNotice(notice, snapshot.ConnectFailed || snapshot.DisconnectFailed);
    }

    // Re-raise the host-derived hint after the shell recomputes HasConfigs on a snapshot.
    public void NotifyHostFlagsChanged()
    {
        OnPropertyChanged(nameof(ShowSelectConfigHint));
    }

    partial void OnRestartPendingChanged(bool value)
    {
        _host.NotifyRestartPendingChanged(value);
    }

    partial void OnActiveConfigChanged(ConfigItemViewModel? oldValue, ConfigItemViewModel? newValue)
    {
        SyncActiveConfigChoice();
        NotifyCanToggleConnection();
        _host.Config.NotifyActiveConfigChanged();

        if (_suppressActivePush || newValue is null)
        {
            return;
        }

        _ = SelectConfigAsync(newValue.Name);
        _prefs.LastConfig = newValue.Name;
        _prefs.Save();
    }

    private void NotifyCanToggleConnection()
    {
        OnPropertyChanged(nameof(CanToggleConnection));
        OnPropertyChanged(nameof(ConnectHint));
        OnPropertyChanged(nameof(ShowSelectConfigHint));
        ToggleConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnActiveConfigChoiceChanged(ConfigChoice? value)
    {
        if (_suppressActiveChoice || value is null)
        {
            return;
        }

        ActiveConfig = value.IsReal
            ? _host.Config.Configs.FirstOrDefault(b => string.Equals(b.Name, value.Name, StringComparison.Ordinal))
            : null;
    }

    // Mirror the connection card's active config into its combo without echoing the pick back. Called by the
    // config screen after its snapshot reconcile, so the choice tracks a renamed/removed active config.
    public void SyncActiveConfigChoice()
    {
        _suppressActiveChoice = true;
        ActiveConfigChoice = ActiveConfig is null
            ? ConfigChoice.None
            : _host.Config.ConfigCatalogueOptions.FirstOrDefault(o => o.IsReal && string.Equals(o.Name, ActiveConfig.Name, StringComparison.Ordinal)) ?? ConfigChoice.None;
        _suppressActiveChoice = false;
    }

    [RelayCommand(CanExecute = nameof(CanToggleConnection))]
    private async Task ToggleConnection()
    {
        var connect = !IsTunnelActive;
        IsTunnelActive = connect;
        BoundStatus = connect ? ConnectionStatus.Connecting : ConnectionStatus.Disconnecting;
        _toggleInFlight = true;
        try
        {
            // Select the config shown in the combo BEFORE dialing, so the agent's target is the one the user
            // sees - not its previously-latched/persisted target (which may be empty, a different config, or
            // a deleted one). Idempotent if already selected. Mirrors ToggleConfigConnectionAsync.
            if (connect && ActiveConfig is not null)
            {
                await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectConfig, [ActiveConfig.Name]));
            }

            var ack = await _connection.SendCommandAsync(
                new IpcCommand(IpcContract.OpSetConnection, [connect ? "connect" : "disconnect"]));
            if (!ack.Ok)
            {
                IsTunnelActive = !connect;
                if (connect && OwnedByOtherAck(ack))
                {
                    PromptTakeover();
                }
            }
        }
        finally
        {
            _toggleInFlight = false;
        }
    }

    internal async Task SelectConfigAsync(string config)
    {
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectConfig, [config]));
    }

    // Per-config connect/disconnect from a config's detail. Connecting first selects the config, then
    // connects: the agent latches the new target on connect and the supervisor switches a live tunnel to
    // it (tears the old one down, brings this one up). Optimistic state mirrors ToggleConnection so the
    // header power control does not flicker while the switch is in flight.
    internal async Task ToggleConfigConnectionAsync(string config, bool connect)
    {
        IsTunnelActive = connect;
        BoundStatus = connect ? ConnectionStatus.Connecting : ConnectionStatus.Disconnecting;
        _toggleInFlight = true;
        try
        {
            if (connect)
            {
                await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectConfig, [config]));
                var ack = await _connection.SendCommandAsync(
                    new IpcCommand(IpcContract.OpSetConnection, ["connect"]));
                if (!ack.Ok)
                {
                    IsTunnelActive = false;
                    if (OwnedByOtherAck(ack))
                    {
                        PromptTakeover();
                    }
                }
            }
            else
            {
                var ack = await _connection.SendCommandAsync(
                    new IpcCommand(IpcContract.OpSetConnection, ["disconnect"]));
                if (!ack.Ok)
                {
                    IsTunnelActive = true;
                }
            }
        }
        finally
        {
            _toggleInFlight = false;
        }
    }

    // True when the selected config and the running (bound) one differ, so the live tunnel does not match
    // what the card shows.
    private static bool SelectedDiffersFromBound(StatusSnapshot snapshot) =>
        snapshot.SelectedTarget is not null
        && !string.Equals(snapshot.SelectedTarget, snapshot.BoundTarget, StringComparison.Ordinal);

    // Maps the agent's classified failure reason to a localized notice.
    private static string ConnectFailureNotice(StatusSnapshot snapshot)
    {
        var key = ConnectFailureKey(snapshot.ConnectFailReason);
        return NoticeUsesDetail(key)
            ? Loc.Instance.Get(key, snapshot.ConnectFailDetail)
            : Loc.Instance.Get(key);
    }

    // Reason token -> notice resource key; unknown or unclassified falls back to the generic message.
    private static string ConnectFailureKey(string reasonToken)
    {
        return reasonToken switch
        {
            "ConfigMissing" => "MainVm_NoticeConnectFailed_ConfigMissing",
            "ServiceStartFailed" => "MainVm_NoticeConnectFailed_ServiceStartFailed",
            "ServiceLaunchFailed" => "MainVm_NoticeConnectFailed_ServiceLaunchFailed",
            "UnderlayUnreachable" => "MainVm_NoticeConnectFailed_UnderlayUnreachable",
            "AdapterStartFailed" => "MainVm_NoticeConnectFailed_AdapterStartFailed",
            "NoHandshake" => "MainVm_NoticeConnectFailed_NoHandshake",
            "TransportRejected" => "MainVm_NoticeConnectFailed_TransportRejected",
            "Timeout" => "MainVm_NoticeConnectFailed_Timeout",
            "NoTargetSelected" => "MainVm_NoticeConnectFailed_NoTargetSelected",
            "ConfigInvalid" => "MainVm_NoticeConnectFailed_ConfigInvalid",
            "PermissionDenied" => "MainVm_NoticeConnectFailed_PermissionDenied",
            "TooManyRoutes" => "MainVm_NoticeConnectFailed_TooManyRoutes",
            "TunnelSetupFailed" => "MainVm_NoticeConnectFailed_TunnelSetupFailed",
            "EngineStartFailed" => "MainVm_NoticeConnectFailed_EngineStartFailed",
            "EngineUnavailable" => "MainVm_NoticeConnectFailed_EngineUnavailable",
            _ => "MainVm_NoticeConnectFailed",
        };
    }

    // Notice keys that format the {0} detail.
    private static bool NoticeUsesDetail(string key)
    {
        return key is "MainVm_NoticeConnectFailed_ServiceStartFailed"
            or "MainVm_NoticeConnectFailed_ServiceLaunchFailed"
            or "MainVm_NoticeConnectFailed_TransportRejected"
            or "MainVm_NoticeConnectFailed_TooManyRoutes"
            or "MainVm_NoticeConnectFailed_TunnelSetupFailed"
            or "MainVm_NoticeConnectFailed_EngineUnavailable";
    }

    /// <summary>
    /// Shows a notice banner. The reconnect banner holds until acted on; other notices auto-hide after 5
    /// seconds. Re-arms only when the notice text changes, so a persistent condition is not re-shown on
    /// every snapshot (and a dismissed banner stays dismissed until a different notice arrives).
    /// </summary>
    public void ShowNotice(string? notice, bool persistent = false)
    {
        if (string.Equals(notice, _lastNotice, StringComparison.Ordinal))
        {
            return;
        }

        _lastNotice = notice;
        NoticeText = notice;
        _noticeTimer.Stop();
        if (notice is null)
        {
            NoticeVisible = false;
            return;
        }

        NoticeVisible = true;
        // The reconnect / failure banner stays up until acted on or the condition clears; other notices auto-hide.
        if (!ReconnectAvailable && !persistent)
        {
            _noticeTimer.Start();
        }
    }

    [RelayCommand]
    private void DismissNotice()
    {
        TakeoverPending = false;
        _noticeTimer.Stop();
        NoticeVisible = false;
    }

    /// <summary>
    /// Reconnects the tunnel from the notice banner: disconnect, wait for teardown, then connect the active config.
    /// </summary>
    [RelayCommand]
    private async Task Reconnect()
    {
        // A takeover prompt reuses this banner action: switch the tunnel to this account instead of reconnecting.
        if (TakeoverPending)
        {
            TakeoverPending = false;
            await TakeoverAsync();
            return;
        }

        // Gate the power toggle for the duration so a mid-wait connect/disconnect can't wedge the teardown wait.
        // The banner is left to clear on its own: RestartRequired drops after the real disconnect, so the notice
        // goes null on the next snapshot; a failed disconnect leaves it standing instead of vanishing silently.
        Reconnecting = true;
        try
        {
            // A stalled disconnect (#14) is retried as a plain disconnect: the tunnel is still up, not to be re-dialed.
            if (DisconnectFailed)
            {
                await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetConnection, ["disconnect"]));
                return;
            }

            // A live tunnel is torn down first; a failed connect (#11) is already down, so skip straight to the dial.
            if (ConnState != 0)
            {
                var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetConnection, ["disconnect"]));
                if (!ack.Ok)
                {
                    return;
                }

                await WaitForDisconnectAsync();
            }

            if (ActiveConfig is not null)
            {
                await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectConfig, [ActiveConfig.Name]));
            }

            await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetConnection, ["connect"]));
        }
        finally
        {
            Reconnecting = false;
        }
    }

    // Waits for the snapshot-driven state to reach disconnected, bounded to 15s so a stuck teardown still dials.
    private async Task WaitForDisconnectAsync()
    {
        for (var i = 0; i < 75 && ConnState != 0; i++)
        {
            await Task.Delay(200);
        }
    }

    // Switches the single machine-wide tunnel to this account after the owned-by-other prompt is accepted.
    private async Task TakeoverAsync()
    {
        IsTunnelActive = true;
        BoundStatus = ConnectionStatus.Connecting;
        _toggleInFlight = true;
        try
        {
            if (ActiveConfig is not null)
            {
                await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectConfig, [ActiveConfig.Name]));
            }

            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetConnection, ["connect", "takeover"]));
            if (!ack.Ok)
            {
                IsTunnelActive = false;
            }
        }
        finally
        {
            _toggleInFlight = false;
        }
    }

    /// <summary>
    /// Raises the takeover prompt from outside a live connect attempt (a tray connect the agent already refused as
    /// a non-owner routed the user here).
    /// </summary>
    public void RequestTakeover()
    {
        PromptTakeover();
    }

    // Raises the takeover prompt on the notice banner; its action re-sends connect with the takeover flag.
    private void PromptTakeover()
    {
        TakeoverPending = true;
        ReconnectAvailable = true;
        ShowNotice(Loc.Instance.Get("MainVm_NoticeTunnelOwnedByOther"), persistent: true);
    }

    // True when an ack signals the tunnel is owned by another account.
    private static bool OwnedByOtherAck(IpcAck ack)
    {
        return IpcMessage.TryParse(ack.Message, out var key, out _)
            && string.Equals(key, "Agent_TunnelOwnedByOther", StringComparison.Ordinal);
    }
}
