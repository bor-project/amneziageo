using Avalonia.Media;
using Avalonia.Threading;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Home screen: the connection card (power control, status, active-profile picker), the tray-icon colour, and
/// the top-center notice banner. The profile catalogue and the config-completeness flags live on the shell,
/// reached through <c>_host</c>.
/// </summary>
internal sealed partial class ConnectionViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _host;
    private readonly AgentConnection _connection;
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
    private ProfileItemViewModel? _activeProfile;

    [ObservableProperty]
    private ProfileChoice? _activeProfileChoice = ProfileChoice.None;

    // False until the first snapshot lands, so the card shows a loader instead of the indeterminate button.
    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private bool _noticeVisible;

    [ObservableProperty]
    private string? _noticeText;

    [ObservableProperty]
    private bool _reconnectAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    private bool _reconnecting;

    /// <summary>
    /// ctor
    /// </summary>
    public ConnectionViewModel(MainWindowViewModel host, AgentConnection connection, UiPreferences prefs)
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
            _ => StatusLabels.Text(ConnectionStatus.Disconnected),
        }
        : Loc.Instance.Get("MainVm_NoAgentConnection");

    public bool CanToggleConnection => !Reconnecting && IsConnected && (IsTunnelActive || (ActiveProfile is { IsComplete: true }));

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
        1 => Loc.Instance.Get("MainVm_ConnectHintConnecting"),
        2 => Loc.Instance.Get("MainVm_ConnectHintClickToDisconnect"),
        _ when ActiveProfile is null => Loc.Instance.Get("MainVm_ConnectHintSelectProfile"),
        _ when ActiveProfile is { IsComplete: false } => Loc.Instance.Get("MainVm_ConnectHintNoConfig"),
        _ => Loc.Instance.Get("MainVm_ConnectHintClickToConnect"),
    };

    public bool ShowSelectConfigHint => ConnState == 0 && _host.HasProfiles && ActiveProfile is not { IsComplete: true };

    public string ConnectPillContent => IsTunnelActive ? Loc.Instance.Get("MainVm_Disconnect") : Loc.Instance.Get("MainVm_Connect");

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
    /// Tears down the connection card on disconnect: clears the live state, the active profile, and the notice.
    /// </summary>
    public void Reset()
    {
        IsConnected = false;
        BoundStatus = ConnectionStatus.Disconnected;
        // The active profile's rows were dropped by Profile.Reset, so its combo re-mirrors to «— не выбрано —»
        // and connect re-gates until the next reconnect snapshot.
        ActiveProfile = null;
        BoundTarget = null;
        _noticeTimer.Stop();
        _lastNotice = null;
        NoticeVisible = false;
        NoticeText = null;
        ReconnectAvailable = false;
    }

    /// <summary>
    /// Applies the connection state, active-profile matching, and top-center notice from the snapshot. Runs
    /// after the profile catalogue is reconciled, so the matching reads the fresh rows.
    /// </summary>
    public void Apply(StatusSnapshot snapshot)
    {
        // First snapshot: the card leaves the loading state for the real connection UI.
        IsReady = true;
        BoundTarget = snapshot.BoundTarget;
        BoundStatus = snapshot.BoundStatus;
        if (!_toggleInFlight)
        {
            IsTunnelActive = snapshot.Active;
        }

        // The agent stores the selected/bound target as EITHER a profile name or the bare config name the
        // profile wraps (a legacy `set-profile <config>`, a preconfigured "main" seed, or a target set out
        // of band). A profile's name and its config name never coincide - they share one namespace - so we
        // match on either; otherwise the current target lights up no row at all (the reported bug).
        var selected = snapshot.SelectedTarget ?? snapshot.BoundTarget;
        foreach (var item in _host.Profile.Profiles)
        {
            item.IsActive = ProfileMatchesTarget(item, selected);
            // A DIFFERENT profile is the live tunnel: this profile's connect button reads "Переключить".
            item.OtherActive = snapshot.Active && !ProfileMatchesTarget(item, snapshot.BoundTarget);
        }

        // Mirror the agent's selected target into the connection-card profile combo without echoing a select
        // back. Prefer the agent's active/selected target; fall back to the last profile the user had
        // chosen (restored from prefs) so the window opens on it with connect still gated until present.
        _suppressActivePush = true;
        var active = _host.Profile.Profiles.FirstOrDefault(b => ProfileMatchesTarget(b, selected));
        if (active is null && !string.IsNullOrEmpty(_prefs.LastProfile))
        {
            active = _host.Profile.Profiles.FirstOrDefault(b => string.Equals(b.Name, _prefs.LastProfile, StringComparison.Ordinal));
        }
        if (active is not null)
        {
            ActiveProfile = active;
        }
        else if (ActiveProfile is not null && !_host.Profile.Profiles.Contains(ActiveProfile))
        {
            // The chosen profile was removed elsewhere: drop the selection so connect re-gates.
            ActiveProfile = null;
        }
        _suppressActivePush = false;

        // Top-center notice (auto-hides after 5s, dismissable): a different profile is selected while a
        // tunnel is up (reconnect to apply - no auto-switch), settings changed on a live tunnel, or a
        // connect failure. Shown once per distinct notice, not re-armed while the same one holds.
        string? notice = null;
        var reconnect = false;
        if (snapshot.ConnectFailed)
        {
            notice = ConnectFailureNotice(snapshot);
        }
        else if (snapshot.Active && SelectedDiffersFromBound(snapshot))
        {
            notice = Loc.Instance.Get("MainVm_NoticeProfileSelected", snapshot.SelectedTarget);
        }
        else if (snapshot.RestartRequired)
        {
            // Settings changed on the live tunnel: bound == selected, so reconnecting the active profile applies them.
            notice = Loc.Instance.Get("MainVm_NoticeSettingsChanged");
            reconnect = true;
        }

        ReconnectAvailable = reconnect;
        ShowNotice(notice);
    }

    // Re-raise the host-derived hint after the shell recomputes HasProfiles on a snapshot.
    public void NotifyHostFlagsChanged()
    {
        OnPropertyChanged(nameof(ShowSelectConfigHint));
    }

    partial void OnActiveProfileChanged(ProfileItemViewModel? oldValue, ProfileItemViewModel? newValue)
    {
        // Track the active profile's completeness.
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnActiveProfilePropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnActiveProfilePropertyChanged;
        }

        SyncActiveProfileChoice();
        NotifyCanToggleConnection();

        if (_suppressActivePush || newValue is null)
        {
            return;
        }

        _ = SelectProfileAsync(newValue.Name);
        _prefs.LastProfile = newValue.Name;
        _prefs.Save();
    }

    private void OnActiveProfilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProfileItemViewModel.Config) or nameof(ProfileItemViewModel.IsComplete))
        {
            NotifyCanToggleConnection();
        }
    }

    private void NotifyCanToggleConnection()
    {
        OnPropertyChanged(nameof(CanToggleConnection));
        OnPropertyChanged(nameof(ConnectHint));
        OnPropertyChanged(nameof(ShowSelectConfigHint));
        ToggleConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnActiveProfileChoiceChanged(ProfileChoice? value)
    {
        if (_suppressActiveChoice || value is null)
        {
            return;
        }

        ActiveProfile = value.IsReal
            ? _host.Profile.Profiles.FirstOrDefault(b => string.Equals(b.Name, value.Identity, StringComparison.Ordinal))
            : null;
    }

    // Mirror the connection card's active profile into its combo without echoing the pick back. Called by the
    // profile screen after its snapshot reconcile, so the choice tracks a renamed/removed active profile.
    public void SyncActiveProfileChoice()
    {
        _suppressActiveChoice = true;
        ActiveProfileChoice = ActiveProfile is null
            ? ProfileChoice.None
            : _host.Profile.ProfileOptions.FirstOrDefault(o => o.IsReal && string.Equals(o.Identity, ActiveProfile.Name, StringComparison.Ordinal)) ?? ProfileChoice.None;
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
            // Select the profile shown in the combo BEFORE dialing, so the agent's target is the one the user
            // sees - not its previously-latched/persisted target (which may be empty, a different profile, or
            // a deleted one). Idempotent if already selected. Mirrors ToggleProfileConnectionAsync.
            if (connect && ActiveProfile is not null)
            {
                await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectProfile, [ActiveProfile.Name]));
            }

            var ack = await _connection.SendCommandAsync(
                new IpcCommand(IpcContract.OpSetConnection, [connect ? "connect" : "disconnect"]));
            if (!ack.Ok)
            {
                IsTunnelActive = !connect;
            }
        }
        finally
        {
            _toggleInFlight = false;
        }
    }

    internal async Task SelectProfileAsync(string profile)
    {
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectProfile, [profile]));
    }

    // Per-profile connect/disconnect from a profile's detail. Connecting first selects the profile, then
    // connects: the agent latches the new target on connect and the supervisor switches a live tunnel to
    // it (tears the old one down, brings this one up). Optimistic state mirrors ToggleConnection so the
    // header power control does not flicker while the switch is in flight.
    internal async Task ToggleProfileConnectionAsync(string profile, bool connect)
    {
        IsTunnelActive = connect;
        BoundStatus = connect ? ConnectionStatus.Connecting : ConnectionStatus.Disconnecting;
        _toggleInFlight = true;
        try
        {
            if (connect)
            {
                await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectProfile, [profile]));
                var ack = await _connection.SendCommandAsync(
                    new IpcCommand(IpcContract.OpSetConnection, ["connect"]));
                if (!ack.Ok)
                {
                    IsTunnelActive = false;
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

    // A profile "is" the given target when the target equals its name or the config it wraps. The agent's
    // selected/bound target can be stored as either form, so the UI resolves both to the same profile row.
    private static bool ProfileMatchesTarget(ProfileItemViewModel item, string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        return string.Equals(item.Name, target, StringComparison.Ordinal)
            || (item.Config.Length > 0 && string.Equals(item.Config, target, StringComparison.Ordinal));
    }

    // True only when the selected target and the running (bound) target denote DIFFERENT profiles. When one
    // is a profile name and the other is that same profile's config name they resolve to the same row, so
    // selecting the profile that is already live raises no stray "reconnect to apply" notice.
    private bool SelectedDiffersFromBound(StatusSnapshot snapshot)
    {
        if (snapshot.SelectedTarget is null
            || string.Equals(snapshot.SelectedTarget, snapshot.BoundTarget, StringComparison.Ordinal))
        {
            return false;
        }

        var selectedProfile = _host.Profile.Profiles.FirstOrDefault(b => ProfileMatchesTarget(b, snapshot.SelectedTarget));
        var boundProfile = _host.Profile.Profiles.FirstOrDefault(b => ProfileMatchesTarget(b, snapshot.BoundTarget));
        return !(selectedProfile is not null && ReferenceEquals(selectedProfile, boundProfile));
    }

    // Maps the agent's classified failure reason to a localized notice; a memberless profile keeps its own
    // actionable message regardless of the reason.
    private static string ConnectFailureNotice(StatusSnapshot snapshot)
    {
        var emptyProfile = snapshot.SelectedTarget is not null
            && snapshot.Profiles.FirstOrDefault(b =>
                   string.Equals(b.Name, snapshot.SelectedTarget, StringComparison.Ordinal)) is { Config.Length: 0 };
        if (emptyProfile)
        {
            return Loc.Instance.Get("MainVm_NoticeProfileEmpty", snapshot.SelectedTarget);
        }

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
            "Timeout" => "MainVm_NoticeConnectFailed_Timeout",
            _ => "MainVm_NoticeConnectFailed",
        };
    }

    // Notice keys that format the {0} detail.
    private static bool NoticeUsesDetail(string key)
    {
        return key is "MainVm_NoticeConnectFailed_ServiceStartFailed"
            or "MainVm_NoticeConnectFailed_ServiceLaunchFailed";
    }

    /// <summary>
    /// Shows a notice banner. The reconnect banner holds until acted on; other notices auto-hide after 5
    /// seconds. Re-arms only when the notice text changes, so a persistent condition is not re-shown on
    /// every snapshot (and a dismissed banner stays dismissed until a different notice arrives).
    /// </summary>
    public void ShowNotice(string? notice)
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
        // The reconnect banner stays up until acted on or the condition clears; other notices auto-hide.
        if (!ReconnectAvailable)
        {
            _noticeTimer.Start();
        }
    }

    [RelayCommand]
    private void DismissNotice()
    {
        _noticeTimer.Stop();
        NoticeVisible = false;
    }

    /// <summary>
    /// Reconnects the tunnel from the notice banner: disconnect, wait for teardown, then connect the active profile.
    /// </summary>
    [RelayCommand]
    private async Task Reconnect()
    {
        // Gate the power toggle for the duration so a mid-wait connect/disconnect can't wedge the teardown wait.
        // The banner is left to clear on its own: RestartRequired drops after the real disconnect, so the notice
        // goes null on the next snapshot; a failed disconnect leaves it standing instead of vanishing silently.
        Reconnecting = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetConnection, ["disconnect"]));
            if (!ack.Ok)
            {
                return;
            }

            await WaitForDisconnectAsync();

            if (ActiveProfile is not null)
            {
                await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectProfile, [ActiveProfile.Name]));
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
}
