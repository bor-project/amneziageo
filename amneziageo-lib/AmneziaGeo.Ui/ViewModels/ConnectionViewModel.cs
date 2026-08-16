using System.Net.NetworkInformation;
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
    private readonly DispatcherTimer _networkTimer;

    private bool _toggleInFlight;

    // The configuration a dial is heading for, until the tunnel binds it.
    private string? _dialTarget;
    private CancellationTokenSource? _probeCts;
    private bool _probedOnce;
    private string? _lastNotice;
    private bool _suppressActivePush;
    private bool _suppressActiveChoice;
    // Set while an unpick is in flight: a snapshot taken before the agent heard it still names the old target.
    private bool _clearingActive;

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

    // The row the user picked in the server table: it wears the frame, and while nothing runs it is also the
    // configuration the connect control dials.
    [ObservableProperty]
    private ConfigItemViewModel? _selectedRow;

    // False until the first snapshot lands, so the card shows a loader instead of the indeterminate button.
    [ObservableProperty]
    private bool _isReady;

    // A measurement of every server is in flight.
    [ObservableProperty]
    private bool _probeRunning;

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
        _alwaysOnMode = prefs.AlwaysOnMode;
        Loc.Instance.CultureChanged += OnCultureChanged;
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            NoticeVisible = false;
        };
        _networkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _networkTimer.Tick += (_, _) =>
        {
            _networkTimer.Stop();
            ProbeOnHomeShown();
        };
        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        }
        catch (Exception)
        {
            // A platform that reports no network events leaves the button and the home screen as the triggers.
        }
    }

    // An interface came up, went down, or changed address: what the table shows no longer holds. A burst of
    // events is coalesced into one measurement, and only the screen that shows the table pays for it.
    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_host.IsHome)
            {
                _networkTimer.Stop();
                _networkTimer.Start();
            }
        });
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
    // In always-on mode the button only leads to the system screen, so nothing about the tunnel bars it.
    public bool CanToggleConnection => AlwaysOnRouting
        ? IsConnected
        : !Reconnecting && !DisconnectFailed && IsConnected && (IsTunnelActive || ActiveConfig is not null);

    // Kept off the screen for now; the switch and everything it drives stay in place.
    private const bool AlwaysOnToggleOffered = false;

    /// <summary>
    /// Whether the always-on switch is offered on the home screen: the system screen it leads to is Android's,
    /// and a television has no room for it on the pad path.
    /// </summary>
    public static bool ShowAlwaysOnToggle => AlwaysOnToggleOffered && OperatingSystem.IsAndroid() && !UiPlatform.IsTelevision;

    // The mode bites only while the switch is on the screen: hidden, the button dials as it always did.
    private bool AlwaysOnRouting => ShowAlwaysOnToggle && AlwaysOnMode;

    /// <summary>
    /// Whether the power button leads to the system always-on screen instead of dialling. Always-on belongs to
    /// the system: no application may switch it on for itself.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    private bool _alwaysOnMode;

    partial void OnAlwaysOnModeChanged(bool value)
    {
        _prefs.AlwaysOnMode = value;
        _prefs.Save();
    }

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

    // The row the tunnel runs on, the only one carrying live link numbers.
    private ConfigItemViewModel? BoundRow =>
        _host.Config.Configs.FirstOrDefault(c => string.Equals(c.Name, BoundTarget, StringComparison.Ordinal));

    // The running tunnel has heard nothing from its server for longer than a rekey window: up, but dead.
    public bool ServerSilent => ConnState == 2 && BoundRow is { LinkSilent: true };

    /// <summary>
    /// Whether the home screen shows what the running tunnel carries.
    /// </summary>
    public bool ShowLink => ConfigItemViewModel.SpeedShown && ConnState == 2 && BoundRow is not null;

    /// <summary>
    /// Receive and send rates of the running tunnel.
    /// </summary>
    public string LinkSpeedText => BoundRow?.LinkSpeedText ?? string.Empty;

    /// <summary>
    /// Whether the running tunnel keeps re-establishing its session instead of carrying traffic.
    /// </summary>
    public bool LinkChurning => ConnState == 2 && BoundRow is { LinkChurning: true };

    /// <summary>
    /// How many sessions a minute the running tunnel burns.
    /// </summary>
    public string LinkChurnText => BoundRow?.LinkChurnText ?? string.Empty;

    /// <summary>
    /// Colour of the re-establish warning.
    /// </summary>
    public IBrush LinkChurnBrush => _orange;

    // Whether the resolver this machine sends its lookups to stopped answering.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNamesUnrouted))]
    [NotifyPropertyChangedFor(nameof(ConnectStatusBrush))]
    [NotifyPropertyChangedFor(nameof(TrayStatusColor))]
    private bool _namesUnrouted;

    /// <summary>
    /// Whether the running tunnel resolves no names here, so its rules by domain no longer apply.
    /// </summary>
    public bool ShowNamesUnrouted => ConnState == 2 && NamesUnrouted;

    /// <summary>
    /// What a silent resolver costs, and what puts it back.
    /// </summary>
    public string NamesUnroutedText => Loc.Instance.Get("Main_NamesUnrouted");

    /// <summary>
    /// Colour of the silent-resolver warning.
    /// </summary>
    public IBrush NamesUnroutedBrush => _orange;

    /// <summary>
    /// Whether the home screen shows what the running tunnel loses.
    /// </summary>
    public bool ShowLinkLoss => ConfigItemViewModel.LinkShown && ConnState == 2 && BoundRow is { ShowLinkLoss: true };

    /// <summary>
    /// The share of the running tunnel's own probes that never came back.
    /// </summary>
    public string LinkLossText => BoundRow?.LinkLossText ?? string.Empty;

    /// <summary>
    /// Colour of the loss line: the hint it is written in while the link is clean, the warning colour once it
    /// drops enough to be felt.
    /// </summary>
    public IBrush LinkLossBrush => BoundRow is { LinkLossy: true } ? _orange : _hintBrush;

    public string ConnectHint => ServerSilent
        ? Loc.Instance.Get("MainVm_ConnectHintServerSilent")
        : ConnState switch
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

    // Colour per state: disconnected grey, transitioning (connect / disconnect) orange, connected blue. A
    // tunnel whose server has gone silent takes the same orange as a switch in flight - it is up, but carries
    // nothing.
    public IBrush ConnectCircleBrush => ConnState == 2 && !ServerSilent ? _circleBlue : Brushes.White;

    public IBrush ConnectCircleBorderBrush => ServerSilent ? _orange : ConnState switch
    {
        2 => Brushes.Transparent,
        1 => _orange,
        _ => _circleBorderGray,
    };

    public IBrush ConnectCircleForeground => ServerSilent ? _orange : ConnState switch
    {
        2 => Brushes.White,
        1 => _orange,
        _ => _glyphGray,
    };

    public IBrush ConnectStatusBrush => ServerSilent || ShowNamesUnrouted ? _orange : ConnState switch
    {
        2 => _textBlue,
        1 => _orange,
        _ => _textGray,
    };

    public IBrush ConnectHintBrush => _hintBrush;

    public Color TrayStatusColor => ServerSilent || ShowNamesUnrouted ? Color.FromRgb(0xE0, 0x90, 0x2F) : ConnState switch
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
        _probeCts?.Cancel();
        _probedOnce = false;
        ProbeRunning = false;
        _noticeTimer.Stop();
        _lastNotice = null;
        NoticeVisible = false;
        NoticeText = null;
        ReconnectAvailable = false;
        RestartPending = false;
        _dialTarget = null;
        ConnectFailed = false;
        DisconnectFailed = false;
        TakeoverPending = false;
        NamesUnrouted = false;
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
        NamesUnrouted = snapshot.DnsUnreachable;
        if (!_toggleInFlight)
        {
            IsTunnelActive = snapshot.Active;
        }

        ApplySelection(snapshot);

        // First catalogue in hand: measure every server once, so the home list opens carrying real numbers.
        if (!_probedOnce && _host.HasConfigs)
        {
            _probedOnce = true;
            ProbeAllCommand.Execute(null);
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

        // The dial is over once the tunnel carries its target, the dial fails, or the tunnel goes down.
        if (_dialTarget is not null
            && (!snapshot.Active || snapshot.ConnectFailed
                || string.Equals(snapshot.BoundTarget, _dialTarget, StringComparison.Ordinal)))
        {
            _dialTarget = null;
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
        else if (snapshot.Active && !_toggleInFlight && _dialTarget is null
            && string.Equals(snapshot.BoundStatus, ConnectionStatus.Connected, StringComparison.Ordinal)
            && SelectedDiffersFromBound(snapshot))
        {
            // A different config is selected on the live tunnel: reuse the reconnect banner so its action applies
            // the switch (Reconnect dials the newly selected ActiveConfig), like the settings-changed case below.
            // A switch names the new target while the old one still runs, so the banner waits for the tunnel to
            // carry it rather than blinking through every switch.
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

        // The keepalive age arrived with the rows, so re-colour the connect control from it.
        NotifyServerSilentChanged();

        ConnectFailed = snapshot.ConnectFailed;
        DisconnectFailed = snapshot.DisconnectFailed;
        ReconnectAvailable = reconnect;
        // A failed dial (or a stalled disconnect) keeps its banner up until the next command, like the reconnect
        // banner, so a boot auto-connect failure with no window is not lost once the tray balloon fades.
        ShowNotice(notice, snapshot.ConnectFailed || snapshot.DisconnectFailed);
    }

    // Matches the agent's selected target against the config rows. Skipped while an unpick is in flight: that
    // snapshot predates the command and would put the config the user just dropped straight back.
    private void ApplySelection(StatusSnapshot snapshot)
    {
        if (_clearingActive)
        {
            return;
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

        // Deleting the selected config hands the selection to the next remaining one. Nothing else picks a
        // config here: leaving none selected is the user's choice, and re-picking would undo it every snapshot.
        // Set outside the echo-suppression so it persists like a manual pick.
        if (ActiveConfig is null && selectionLost && _host.Config.Configs.FirstOrDefault() is { } fallback)
        {
            ActiveConfig = fallback;
        }

        // A rename moves the selection under the UI, and the assignment above is echo-suppressed, so the
        // preference that restores it on the next start would keep the old name and open with none selected.
        if (ActiveConfig is { } current && !string.Equals(_prefs.LastConfig, current.Name, StringComparison.Ordinal))
        {
            _prefs.LastConfig = current.Name;
            _prefs.Save();
        }
    }

    // Re-raise everything the silent-server verdict feeds.
    private void NotifyServerSilentChanged()
    {
        OnPropertyChanged(nameof(ServerSilent));
        OnPropertyChanged(nameof(ShowLink));
        OnPropertyChanged(nameof(LinkSpeedText));
        OnPropertyChanged(nameof(LinkChurning));
        OnPropertyChanged(nameof(LinkChurnText));
        OnPropertyChanged(nameof(ShowNamesUnrouted));
        OnPropertyChanged(nameof(ShowLinkLoss));
        OnPropertyChanged(nameof(LinkLossText));
        OnPropertyChanged(nameof(LinkLossBrush));
        OnPropertyChanged(nameof(ConnectHint));
        OnPropertyChanged(nameof(ConnectCircleBrush));
        OnPropertyChanged(nameof(ConnectCircleBorderBrush));
        OnPropertyChanged(nameof(ConnectCircleForeground));
        OnPropertyChanged(nameof(ConnectStatusBrush));
        OnPropertyChanged(nameof(TrayStatusColor));
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

    // The config section offers its connect link only while nothing runs.
    partial void OnIsTunnelActiveChanged(bool value)
    {
        _host.Config.NotifyActiveConfigChanged();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        _host.Config.NotifyActiveConfigChanged();
    }

    partial void OnSelectedRowChanged(ConfigItemViewModel? oldValue, ConfigItemViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    partial void OnActiveConfigChanged(ConfigItemViewModel? oldValue, ConfigItemViewModel? newValue)
    {
        SelectedRow = newValue;
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

        if (!value.IsReal)
        {
            _ = ClearActiveConfigAsync();
            return;
        }

        ActiveConfig = _host.Config.Configs.FirstOrDefault(b => string.Equals(b.Name, value.Name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Leaves no configuration selected. A live tunnel is bound to the one being unpicked, so it goes down
    /// first; a refused disconnect keeps the selection. The agent is told before the card, so a snapshot still
    /// carrying the old target cannot put it back.
    /// </summary>
    internal async Task ClearActiveConfigAsync()
    {
        if (_clearingActive)
        {
            return;
        }

        _clearingActive = true;
        try
        {
            if (IsTunnelActive && !await DisconnectAsync())
            {
                SyncActiveConfigChoice();
                return;
            }

            await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectConfig, [string.Empty]));
            _suppressActivePush = true;
            ActiveConfig = null;
            _suppressActivePush = false;
            _prefs.LastConfig = string.Empty;
            _prefs.Save();
        }
        finally
        {
            _clearingActive = false;
        }
    }

    /// <summary>
    /// Takes the tunnel down when it carries this configuration, so the agent lets it be removed; reports
    /// whether the configuration is free.
    /// </summary>
    internal async Task<bool> EnsureDisconnectedAsync(string name)
    {
        // Only the configuration the tunnel is bound to: a merely selected one is dropped without touching a
        // tunnel that runs on another.
        if (!IsTunnelActive || !string.Equals(BoundTarget, name, StringComparison.Ordinal))
        {
            return true;
        }

        if (!await DisconnectAsync())
        {
            return false;
        }

        await WaitForDisconnectAsync();
        return true;
    }

    /// <summary>
    /// Moves the selection off a configuration about to be removed: it lands on the first one left, and on none
    /// when that was the last. The agent is told first, so a snapshot still carrying the old target cannot put
    /// it back.
    /// </summary>
    internal async Task MoveSelectionOffAsync(string name)
    {
        if (!string.Equals(ActiveConfig?.Name, name, StringComparison.Ordinal))
        {
            return;
        }

        var next = _host.Config.Configs.FirstOrDefault(c => !string.Equals(c.Name, name, StringComparison.Ordinal));
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectConfig, [next?.Name ?? string.Empty]));
        _suppressActivePush = true;
        ActiveConfig = next;
        _suppressActivePush = false;
        _prefs.LastConfig = next?.Name ?? string.Empty;
        _prefs.Save();
    }

    /// <summary>
    /// Hands the selection to a configuration added into an empty catalogue: there is nothing else to pick, and
    /// its row only arrives with the next snapshot, so the agent is told by name.
    /// </summary>
    internal async Task AdoptFirstConfigAsync(string name)
    {
        if (ActiveConfig is not null)
        {
            return;
        }

        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectConfig, [name]));
        _prefs.LastConfig = name;
        _prefs.Save();
    }

    // Takes the tunnel down and reports whether it went. Optimistic state mirrors ToggleConnection so the
    // header power control does not flicker while the command is in flight.
    private async Task<bool> DisconnectAsync()
    {
        IsTunnelActive = false;
        BoundStatus = ConnectionStatus.Disconnecting;
        _toggleInFlight = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetConnection, ["disconnect"]));
            if (!ack.Ok)
            {
                IsTunnelActive = true;
            }

            return ack.Ok;
        }
        finally
        {
            _toggleInFlight = false;
        }
    }

    // Mirror the connection card's active config into its combo without echoing the pick back. Called by the
    // config screen after its snapshot reconcile, so the choice tracks a renamed/removed active config.
    public void SyncActiveConfigChoice()
    {
        _suppressActiveChoice = true;
        ActiveConfigChoice = ActiveConfig is null
            ? ConfigChoice.None
            : _host.Config.HomeConfigOptions.FirstOrDefault(o => o.IsReal && string.Equals(o.Name, ActiveConfig.Name, StringComparison.Ordinal)) ?? ConfigChoice.None;
        _suppressActiveChoice = false;
    }

    /// <summary>
    /// Picks the row the user tapped and measures its server. A live tunnel keeps its target and its status:
    /// the tap moves the frame alone, and the row's own connect button is what switches the tunnel over.
    /// </summary>
    [RelayCommand]
    private void SelectRow(ConfigItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (IsTunnelActive)
        {
            SelectedRow = item;
        }
        else
        {
            ActiveConfig = item;
        }

        item.Probing = true;
        _ = ProbeRowAsync(item, CancellationToken.None);
    }

    /// <summary>
    /// Connects the configuration a home server row stands for, or takes the tunnel down when the row is the
    /// one already running.
    /// </summary>
    [RelayCommand]
    private async Task ConnectConfig(ConfigItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var live = IsTunnelActive && string.Equals(BoundTarget, item.Name, StringComparison.Ordinal);
        item.ConnectOpen = false;
        ActiveConfig = item;
        await ToggleConfigConnectionAsync(item.Name, !live);
    }

    /// <summary>
    /// Re-measures every server when the home screen is shown again; a run already in flight is left alone.
    /// </summary>
    public void ProbeOnHomeShown()
    {
        if (!ProbeRunning && _host.HasConfigs)
        {
            ProbeAllCommand.Execute(null);
        }
    }

    // Measures every configuration at once; the previous run, if any, is dropped.
    [RelayCommand]
    private void ProbeAll()
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        _ = RunProbeAsync(cts);
    }

    private async Task RunProbeAsync(CancellationTokenSource cts)
    {
        var rows = _host.Config.Configs.ToArray();
        foreach (var row in rows)
        {
            row.Probing = true;
        }

        ProbeRunning = true;
        try
        {
            await Task.WhenAll(rows.Select(row => ProbeRowAsync(row, cts.Token)));
            MarkBest(rows);
        }
        finally
        {
            // A run superseded by a newer one leaves the flag to whoever replaced it.
            if (ReferenceEquals(_probeCts, cts))
            {
                ProbeRunning = false;
            }
        }
    }

    // Names the server the sweep favours: fewest losses first, then the shortest round trip.
    private static void MarkBest(IReadOnlyList<ConfigItemViewModel> rows)
    {
        var best = rows
            .Where(row => row.ProbeState == ProbeOutcome.Alive)
            .OrderBy(row => row.ProbeLossPercent)
            .ThenBy(row => row.ProbeMilliseconds)
            .FirstOrDefault();
        foreach (var row in rows)
        {
            row.IsBest = ReferenceEquals(row, best);
        }
    }

    // Measures one server off the UI thread and posts the answer back into its row.
    private static async Task ProbeRowAsync(ConfigItemViewModel row, CancellationToken ct)
    {
        var result = await EndpointProbe
            .MeasureAsync(row.Endpoint, row.UseWebSocket, row.WebSocketHost, row.WebSocketPort, ct)
            .ConfigureAwait(false);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            row.ProbeState = result.Outcome;
            row.ProbeMilliseconds = result.Milliseconds;
            row.ProbeLossPercent = result.LossPercent;
            row.Probing = false;
        });
    }

    [RelayCommand(CanExecute = nameof(CanToggleConnection))]
    private async Task ToggleConnection()
    {
        // The switch is on: the button leads to the system screen carrying always-on, and dials nothing.
        if (AlwaysOnRouting)
        {
            await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpOpenVpnSettings, []));
            return;
        }

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
                _dialTarget = ActiveConfig.Name;
                await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectConfig, [ActiveConfig.Name]));
            }

            var ack = await _connection.SendCommandAsync(
                new IpcCommand(IpcContract.OpSetConnection, [connect ? "connect" : "disconnect"]));
            if (!ack.Ok)
            {
                _dialTarget = null;
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
                _dialTarget = config;
                await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectConfig, [config]));
                var ack = await _connection.SendCommandAsync(
                    new IpcCommand(IpcContract.OpSetConnection, ["connect"]));
                if (!ack.Ok)
                {
                    _dialTarget = null;
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
        snapshot.SelectedTarget is { Length: > 0 }
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

    /// <summary>
    /// Takes the running tunnel down and waits for the teardown, for an operation the agent refuses while it runs.
    /// </summary>
    public async Task<bool> StopTunnelAsync()
    {
        if (ConnState == 0)
        {
            return true;
        }

        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetConnection, ["disconnect"]));
        if (!ack.Ok)
        {
            return false;
        }

        await WaitForDisconnectAsync();
        return ConnState == 0;
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
