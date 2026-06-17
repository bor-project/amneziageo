using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Top-level view model: connection card, nav state, profile list, routing-list catalogue, and theme.
/// </summary>
internal sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;
    private readonly DispatcherTimer _noticeTimer;
    private IReadOnlyList<string> _configNames = [];
    private bool _toggleInFlight;
    private string? _lastNotice;
    private long _selectedRoutingListId = -1;
    private string? _pendingExpandProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    [NotifyPropertyChangedFor(nameof(AgentStatusBrush))]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    [NotifyPropertyChangedFor(nameof(ConnectButtonBrush))]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    [NotifyPropertyChangedFor(nameof(ConnectGlyph))]
    [NotifyPropertyChangedFor(nameof(ConnectHint))]
    [NotifyPropertyChangedFor(nameof(ConnectStageBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleBorderBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleForeground))]
    [NotifyPropertyChangedFor(nameof(ConnectStatusBrush))]
    private bool _isTunnelActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    [NotifyPropertyChangedFor(nameof(ActiveProfileName))]
    private string? _boundTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    [NotifyPropertyChangedFor(nameof(AgentStatusBrush))]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    [NotifyPropertyChangedFor(nameof(ConnectGlyph))]
    [NotifyPropertyChangedFor(nameof(ConnectHint))]
    [NotifyPropertyChangedFor(nameof(ConnectStageBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleBorderBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleForeground))]
    [NotifyPropertyChangedFor(nameof(ConnectStatusBrush))]
    private string _boundStatus = ConnectionStatus.Disconnected;

    [ObservableProperty]
    private string? _activeMember;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasConfigs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasBalancers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasRoutingLists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome))]
    [NotifyPropertyChangedFor(nameof(IsRouting))]
    [NotifyPropertyChangedFor(nameof(IsSettings))]
    private string _nav = "home";

    // Which settings section the left rail has selected while on the Settings tab.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsGeneral))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSecurity))]
    [NotifyPropertyChangedFor(nameof(IsSettingsAbout))]
    private string _settingsSection = "general";

    // Which routing sub-tab is active: the rule-lists ("rules") or the geo-data sources ("sources").
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRoutingRules))]
    [NotifyPropertyChangedFor(nameof(IsRoutingSources))]
    [NotifyPropertyChangedFor(nameof(ShowRoutingEditor))]
    [NotifyPropertyChangedFor(nameof(ShowRoutingRulesEmpty))]
    private string _routingTab = "rules";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeLabel))]
    private bool _isDark;

    [ObservableProperty]
    private bool _noticeVisible;

    [ObservableProperty]
    private string? _noticeText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoutingEditor))]
    [NotifyPropertyChangedFor(nameof(ShowRoutingEditor))]
    [NotifyPropertyChangedFor(nameof(ShowRoutingRulesEmpty))]
    private RoutingListEditorViewModel? _routingEditor;

    [ObservableProperty]
    private bool _killSwitchEnabled;

    [ObservableProperty]
    private bool _allowLan = true;

    [ObservableProperty]
    private string _newSourceKind = "geosite";

    [ObservableProperty]
    private string _newSourceUrl = string.Empty;

    // True when the kind was inferred from the URL's file name (geosite*/geoip*), so the kind combo is
    // locked to the detected value.
    [ObservableProperty]
    private bool _sourceKindLocked;

    [ObservableProperty]
    private bool _hasSources;

    // The agent activity journal shown on the home screen: newest line first, joined into one string so
    // the view is a single (selectable) text block — no per-line controls to regenerate each push.
    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private bool _hasLogs;

    // Set while applying a snapshot so echoing the agent's current settings into the toggles does not
    // bounce straight back as a set-setting command.
    private bool _suppressSettingPush;

    /// <summary>
    /// ctor
    /// </summary>
    public MainWindowViewModel(AgentConnection connection)
    {
        _connection = connection;
        _connection.Connected += OnConnected;
        _connection.Disconnected += OnDisconnected;
        _connection.SnapshotReceived += OnSnapshot;
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            NoticeVisible = false;
        };
    }

    /// <summary>
    /// The connection used to talk to the agent.
    /// </summary>
    public AgentConnection Connection => _connection;

    /// <summary>
    /// Configuration rows.
    /// </summary>
    public ObservableCollection<ConfigItemViewModel> Configs { get; } = [];

    /// <summary>
    /// Profile rows.
    /// </summary>
    public ObservableCollection<BalancerItemViewModel> Balancers { get; } = [];

    /// <summary>
    /// Routing-list catalogue.
    /// </summary>
    public ObservableCollection<RoutingListSummaryViewModel> RoutingLists { get; } = [];

    /// <summary>
    /// Geo data sources shown on the routing page.
    /// </summary>
    public ObservableCollection<SourceItemViewModel> Sources { get; } = [];

    /// <summary>
    /// The source kinds offered in the add-source form.
    /// </summary>
    public IReadOnlyList<string> SourceKinds { get; } = ["geosite", "geoip"];

    /// <summary>
    /// Banner status text in the connection card.
    /// </summary>
    public string AgentStatusText => IsConnected
        ? ConnState switch
        {
            // Mirror the reconciled ConnState so the label never momentarily contradicts the power
            // circle during a connect / disconnect transition. State 2 keeps the precise status label
            // (Connected vs Degraded); the transient state reads the intent direction.
            2 => StatusLabels.Text(BoundStatus),
            1 => StatusLabels.Text(IsTunnelActive ? ConnectionStatus.Connecting : ConnectionStatus.Disconnecting),
            _ => StatusLabels.Text(ConnectionStatus.Disconnected),
        }
        : "Нет связи с агентом";

    /// <summary>
    /// Connection-card status indicator color.
    /// </summary>
    public IBrush AgentStatusBrush => StatusLabels.Brush(IsConnected ? BoundStatus : ConnectionStatus.Disconnected);

    /// <summary>
    /// Name shown under the status banner in the connection card.
    /// </summary>
    public string ActiveProfileName => BoundTarget ?? "—";

    /// <summary>
    /// Whether nothing is configured yet.
    /// </summary>
    public bool IsEmpty => !HasConfigs && !HasBalancers && !HasRoutingLists;

    /// <summary>
    /// The hint shown when nothing is configured.
    /// </summary>
    public string EmptyHint => "Нет конфигураций. Нажмите «+ Профиль» или «Добавить».";

    /// <summary>
    /// Big connect / disconnect button label, reflecting the agent's desired tunnel state.
    /// </summary>
    public string ConnectButtonText => IsTunnelActive ? "Остановить" : "Запустить";

    /// <summary>
    /// Big connect / disconnect button color.
    /// </summary>
    public IBrush ConnectButtonBrush => StatusLabels.Brush(IsTunnelActive ? ConnectionStatus.Disconnected : ConnectionStatus.Connected);

    /// <summary>
    /// Whether the connect / disconnect button is actionable (the agent pipe is up).
    /// </summary>
    public bool CanToggleConnection => IsConnected;

    // --- Power-button connection control (design "Кнопка-питание"): a round on/off circle with the
    // status and a hint beside it, tinted by state (disconnected / connecting / connected). ---
    private static readonly IBrush _stageOff = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF7));
    private static readonly IBrush _stageConnecting = new SolidColorBrush(Color.FromRgb(0xFD, 0xF6, 0xEC));
    private static readonly IBrush _stageConnected = new SolidColorBrush(Color.FromRgb(0xF0, 0xFA, 0xF4));
    private static readonly IBrush _circleGreen = new SolidColorBrush(Color.FromRgb(0x1F, 0x9D, 0x57));
    private static readonly IBrush _circleBorderGray = new SolidColorBrush(Color.FromRgb(0xD9, 0xDD, 0xE6));
    private static readonly IBrush _circleBorderAmber = new SolidColorBrush(Color.FromRgb(0xF0, 0xD3, 0xA8));
    private static readonly IBrush _glyphGray = new SolidColorBrush(Color.FromRgb(0x7B, 0x81, 0x8D));
    private static readonly IBrush _glyphAmber = new SolidColorBrush(Color.FromRgb(0xE0, 0x90, 0x2F));
    private static readonly IBrush _textGreen = new SolidColorBrush(Color.FromRgb(0x16, 0x7A, 0x44));
    private static readonly IBrush _textAmber = new SolidColorBrush(Color.FromRgb(0xB8, 0x72, 0x1F));
    private static readonly IBrush _textGray = new SolidColorBrush(Color.FromRgb(0x5B, 0x61, 0x6E));
    private static readonly IBrush _hintBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAB));

    // 0 = disconnected, 1 = connecting / disconnecting (transient), 2 = connected. The agent's reported
    // balancer status is reconciled with the desired tunnel state (IsTunnelActive) so a momentarily-stale
    // snapshot cannot flicker the control on click: the instant connect is requested, Active flips true
    // while the balancer status still lags at its previous terminal value ("disconnected") for one push —
    // without this bridge that frame snaps the circle back to the off look before "connecting" arrives.
    // Intent on + a down status reads "connecting"; intent off + an up status reads "disconnecting".
    private int ConnState => BoundStatus switch
    {
        ConnectionStatus.Connected or ConnectionStatus.Degraded => IsTunnelActive ? 2 : 1,
        ConnectionStatus.Connecting or ConnectionStatus.Disconnecting or ConnectionStatus.Failover => 1,
        _ => IsTunnelActive ? 1 : 0,
    };

    /// <summary>Whether the connecting spinner shows in the power circle.</summary>
    public bool IsConnecting => ConnState == 1;

    /// <summary>Glyph in the power circle: ▶ to connect, ■ to stop, empty while connecting (spinner).</summary>
    public string ConnectGlyph => ConnState switch { 1 => string.Empty, 2 => "■", _ => "▶" };

    /// <summary>Hint line under the status in the power control.</summary>
    public string ConnectHint => ConnState switch
    {
        1 => "Устанавливается соединение…",
        2 => "Нажмите, чтобы отключиться",
        _ => "Нажмите, чтобы подключиться",
    };

    /// <summary>Tinted background of the power-control stage.</summary>
    public IBrush ConnectStageBrush => ConnState switch { 2 => _stageConnected, 1 => _stageConnecting, _ => _stageOff };

    /// <summary>Power circle fill.</summary>
    public IBrush ConnectCircleBrush => ConnState == 2 ? _circleGreen : Brushes.White;

    /// <summary>Power circle border.</summary>
    public IBrush ConnectCircleBorderBrush => ConnState switch { 2 => Brushes.Transparent, 1 => _circleBorderAmber, _ => _circleBorderGray };

    /// <summary>Power circle glyph / spinner colour.</summary>
    public IBrush ConnectCircleForeground => ConnState switch { 2 => Brushes.White, 1 => _glyphAmber, _ => _glyphGray };

    /// <summary>Status label colour in the power control.</summary>
    public IBrush ConnectStatusBrush => ConnState switch { 2 => _textGreen, 1 => _textAmber, _ => _textGray };

    /// <summary>Hint label colour in the power control.</summary>
    public IBrush ConnectHintBrush => _hintBrush;

    /// <summary>
    /// Whether the Home tab is shown.
    /// </summary>
    public bool IsHome => Nav == "home";

    /// <summary>
    /// Whether the Routing tab is shown.
    /// </summary>
    public bool IsRouting => Nav == "routing";

    /// <summary>
    /// Whether the Settings tab is shown.
    /// </summary>
    public bool IsSettings => Nav == "settings";

    /// <summary>Whether the General settings section is selected.</summary>
    public bool IsSettingsGeneral => SettingsSection == "general";

    /// <summary>Whether the Security settings section is selected.</summary>
    public bool IsSettingsSecurity => SettingsSection == "security";

    /// <summary>Whether the About settings section is selected.</summary>
    public bool IsSettingsAbout => SettingsSection == "about";

    /// <summary>Whether the routing "rules" sub-tab is active (the rule-lists).</summary>
    public bool IsRoutingRules => RoutingTab != "sources";

    /// <summary>Whether the routing "sources" sub-tab is active (the geo-data bases).</summary>
    public bool IsRoutingSources => RoutingTab == "sources";

    /// <summary>Right pane shows the rule editor: rules sub-tab with a list open.</summary>
    public bool ShowRoutingEditor => IsRoutingRules && HasRoutingEditor;

    /// <summary>Right pane shows the "pick a rule" hint: rules sub-tab, nothing open.</summary>
    public bool ShowRoutingRulesEmpty => IsRoutingRules && !HasRoutingEditor;

    /// <summary>
    /// Whether a routing list is open in the inline editor (drives the routing page master/detail).
    /// </summary>
    public bool HasRoutingEditor => RoutingEditor is not null;

    /// <summary>
    /// Current theme label shown on the toggle button.
    /// </summary>
    public string ThemeLabel => IsDark ? "Тёмная" : "Светлая";

    /// <summary>
    /// Starts the agent connection.
    /// </summary>
    public void Start()
    {
        _connection.Start();
    }

    /// <summary>
    /// Returns the names of the configurations currently known.
    /// </summary>
    public IReadOnlyList<string> ConfigNames()
    {
        return _configNames;
    }

    [RelayCommand]
    private void NavHome()
    {
        Nav = "home";
    }

    [RelayCommand]
    private void NavRouting()
    {
        Nav = "routing";
    }

    [RelayCommand]
    private void NavSettings()
    {
        Nav = "settings";
    }

    [RelayCommand]
    private void SelectSettings(string section)
    {
        SettingsSection = section;
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDark = !IsDark;
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
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

    private void OnConnected()
    {
        Dispatcher.UIThread.Post(() => IsConnected = true);
    }

    private void OnDisconnected()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            BoundStatus = ConnectionStatus.Disconnected;
            Configs.Clear();
            Balancers.Clear();
            RoutingLists.Clear();
            Sources.Clear();
            HasConfigs = false;
            HasBalancers = false;
            HasRoutingLists = false;
            HasSources = false;
            RoutingEditor = null;
            _selectedRoutingListId = -1;
            _configNames = [];
            ActiveMember = null;
            _noticeTimer.Stop();
            _lastNotice = null;
            NoticeVisible = false;
            NoticeText = null;
        });
    }

    private void OnSnapshot(StatusSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => Apply(snapshot));
    }

    private void Apply(StatusSnapshot snapshot)
    {
        BoundTarget = snapshot.BoundTarget;
        BoundStatus = snapshot.BoundStatus;
        if (!_toggleInFlight)
        {
            IsTunnelActive = snapshot.Active;
        }

        SyncConfigs(snapshot.Configs);
        SyncRoutingLists(snapshot.RoutingLists ?? []);
        SyncSources(snapshot.Sources ?? []);
        SyncBalancers(snapshot.Balancers, snapshot.RoutingLists ?? []);
        HasConfigs = Configs.Count > 0;
        HasBalancers = Balancers.Count > 0;
        HasRoutingLists = RoutingLists.Count > 0;
        UpdateRoutingSelection();

        var bound = snapshot.Balancers.FirstOrDefault(b => b.Name == snapshot.BoundTarget);
        ActiveMember = bound?.ActiveMember;

        foreach (var item in Balancers)
        {
            item.IsActive = string.Equals(item.Name, snapshot.SelectedTarget ?? snapshot.BoundTarget, StringComparison.Ordinal);
        }

        // Top-center notice (auto-hides after 5s, dismissable): a different profile is selected while a
        // tunnel is up (reconnect to apply — no auto-switch), settings changed on a live tunnel, or a
        // better member is available on a backup. Shown once per distinct notice, not re-armed while
        // the same one holds.
        string? notice = null;
        if (snapshot.Active && snapshot.SelectedTarget is not null
            && !string.Equals(snapshot.SelectedTarget, snapshot.BoundTarget, StringComparison.Ordinal))
        {
            notice = $"Выбран профиль «{snapshot.SelectedTarget}». Переподключитесь, чтобы применить.";
        }
        else if (snapshot.RestartRequired)
        {
            notice = "Настройки изменены. Переподключитесь, чтобы применить.";
        }
        else if (snapshot.BetterMember is not null)
        {
            notice = $"Доступно приоритетное подключение: {snapshot.BetterMember}. Переподключитесь, чтобы вернуться.";
        }

        ShowNotice(notice);

        _suppressSettingPush = true;
        KillSwitchEnabled = snapshot.KillSwitchEnabled;
        AllowLan = snapshot.AllowLan;
        _suppressSettingPush = false;

        var logs = snapshot.Logs ?? [];
        HasLogs = logs.Count > 0;
        // Newest first so the latest activity stays visible at the top without scrolling.
        LogText = logs.Count == 0 ? string.Empty : string.Join('\n', logs.Reverse());
    }

    /// <summary>
    /// Shows a transient notice banner that auto-hides after 5 seconds. Re-arms only when the notice
    /// text changes, so a persistent condition is not re-shown on every snapshot (and a dismissed
    /// banner stays dismissed until a different notice arrives).
    /// </summary>
    private void ShowNotice(string? notice)
    {
        if (string.Equals(notice, _lastNotice, StringComparison.Ordinal))
        {
            return;
        }

        _lastNotice = notice;
        NoticeText = notice;
        _noticeTimer.Stop();
        if (notice is not null)
        {
            NoticeVisible = true;
            _noticeTimer.Start();
        }
        else
        {
            NoticeVisible = false;
        }
    }

    [RelayCommand]
    private void DismissNotice()
    {
        _noticeTimer.Stop();
        NoticeVisible = false;
    }

    partial void OnKillSwitchEnabledChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync("killswitch", value);
        }
    }

    partial void OnAllowLanChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync("allow-lan", value);
        }
    }

    private async Task SetSettingAsync(string key, bool value)
    {
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting, [key, value ? "on" : "off"]));
    }

    private void SyncConfigs(IReadOnlyList<ConfigEntry> entries)
    {
        // Reconcile in place (match by name) rather than Clear()+Add(): rebuilding the collection on
        // every snapshot push would regenerate every row's controls, flickering the list each tick even
        // though usually only the status field moves during a connect. Update the existing rows instead.
        var present = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = Configs.Count - 1; i >= 0; i--)
        {
            if (!present.Contains(Configs[i].Name))
            {
                Configs.RemoveAt(i);
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var existing = Configs.FirstOrDefault(c => string.Equals(c.Name, entry.Name, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new ConfigItemViewModel { Name = entry.Name };
                Configs.Insert(Math.Min(i, Configs.Count), existing);
            }
            else
            {
                var from = Configs.IndexOf(existing);
                if (from != i)
                {
                    Configs.Move(from, i);
                }
            }

            existing.Endpoint = entry.Endpoint;
            existing.GeoSplit = entry.GeoSplit;
            existing.Rules = entry.Rules;
            existing.Status = entry.Status;
        }

        _configNames = [.. entries.Select(e => e.Name)];
    }

    private void SyncRoutingLists(IReadOnlyList<RoutingListEntry> entries)
    {
        // Reconcile in place (match by id) for the same reason as SyncConfigs / SyncSources, and so the
        // selected-list highlight is not dropped and re-set on every snapshot.
        var present = entries.Select(e => e.Id).ToHashSet();
        for (var i = RoutingLists.Count - 1; i >= 0; i--)
        {
            if (!present.Contains(RoutingLists[i].Id))
            {
                RoutingLists.RemoveAt(i);
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var existing = RoutingLists.FirstOrDefault(r => r.Id == entry.Id);
            if (existing is null)
            {
                existing = new RoutingListSummaryViewModel { Id = entry.Id };
                RoutingLists.Insert(Math.Min(i, RoutingLists.Count), existing);
            }
            else
            {
                var from = RoutingLists.IndexOf(existing);
                if (from != i)
                {
                    RoutingLists.Move(from, i);
                }
            }

            existing.Name = entry.Name;
            existing.RuleCount = entry.RuleCount;
            existing.RouteCount = entry.RouteCount;
            existing.DomainCount = entry.DomainCount;
        }
    }

    private void SyncSources(IReadOnlyList<SourceEntry> entries)
    {
        // Reconcile in place (match by name) rather than Clear()+Add(): rebuilding the collection on
        // every snapshot push would regenerate the row controls and restart the refresh-icon spin
        // animation each tick, making it stutter. Updating fields on the existing rows keeps it smooth.
        var present = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = Sources.Count - 1; i >= 0; i--)
        {
            if (!present.Contains(Sources[i].Name))
            {
                Sources.RemoveAt(i);
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var existing = Sources.FirstOrDefault(s => string.Equals(s.Name, entry.Name, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new SourceItemViewModel(SendUpdateSourceAsync, SendRemoveSourceAsync) { Name = entry.Name };
                Sources.Insert(Math.Min(i, Sources.Count), existing);
            }
            else
            {
                var from = Sources.IndexOf(existing);
                if (from != i)
                {
                    Sources.Move(from, i);
                }
            }

            existing.Kind = entry.Kind;
            existing.Url = entry.Url;
            existing.Updated = entry.Updated;
            existing.CategoryCount = entry.CategoryCount;
            existing.Updating = entry.Updating;
            existing.Progress = entry.Progress;
        }

        HasSources = Sources.Count > 0;
    }

    private void SyncBalancers(IReadOnlyList<BalancerEntry> entries, IReadOnlyList<RoutingListEntry> routingLists)
    {
        var options = BuildRoutingOptions(routingLists);

        // Reconcile in place, matching rows by name, so transient view state (the expanded
        // editor, combo selection) survives the snapshot pushes that follow every edit.
        var present = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = Balancers.Count - 1; i >= 0; i--)
        {
            if (!present.Contains(Balancers[i].Name))
            {
                Balancers.RemoveAt(i);
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var existing = Balancers.FirstOrDefault(b => string.Equals(b.Name, entry.Name, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new BalancerItemViewModel(SaveBalancerAsync, AssignRoutingAsync, SelectProfileAsync, ImportConfigAsync);
                existing.ApplyFromEntry(entry, options, _configNames);
                Balancers.Insert(Math.Min(i, Balancers.Count), existing);
                continue;
            }

            existing.ApplyFromEntry(entry, options, _configNames);
            var index = Balancers.IndexOf(existing);
            if (index != i)
            {
                Balancers.Move(index, i);
            }
        }

        // Auto-expand a profile just created via "+ Профиль" so the user can add configs immediately.
        if (_pendingExpandProfile is not null)
        {
            var created = Balancers.FirstOrDefault(b => string.Equals(b.Name, _pendingExpandProfile, StringComparison.Ordinal));
            if (created is not null)
            {
                created.IsExpanded = true;
                _pendingExpandProfile = null;
            }
        }
    }

    private static IReadOnlyList<RoutingListChoice> BuildRoutingOptions(IReadOnlyList<RoutingListEntry> entries)
    {
        var options = new List<RoutingListChoice> { RoutingListChoice.None };
        foreach (var entry in entries)
        {
            options.Add(new RoutingListChoice(entry.Id, entry.Name));
        }

        return options;
    }

    // "+ Профиль": create an empty profile (no dialog), then auto-expand it so configs can be added.
    [RelayCommand]
    private async Task CreateProfile()
    {
        var name = UniqueProfileName();
        var ack = await _connection.SendCommandAsync(
            new IpcCommand(IpcContract.OpAddBalancer, [name, "60", "priority"]));
        if (ack.Ok)
        {
            _pendingExpandProfile = name;
        }
    }

    private string UniqueProfileName()
    {
        const string baseName = "Новый профиль";
        var existing = Balancers.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private async Task SaveBalancerAsync(string name, int recheck, string mode, IReadOnlyList<string> members)
    {
        var args = new List<string> { name, recheck.ToString(System.Globalization.CultureInfo.InvariantCulture), mode };
        args.AddRange(members);
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpAddBalancer, args));
    }

    private async Task<IpcAck> ImportConfigAsync(string name, string confText)
    {
        return await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpImportConfig, [name, confText]));
    }

    private async Task AssignRoutingAsync(string profile, long? listId, bool useRouting)
    {
        var args = new[]
        {
            profile,
            listId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
            useRouting ? "on" : "off",
        };
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpAssignRouting, args));
    }

    private async Task SelectProfileAsync(string profile)
    {
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectProfile, [profile]));
    }

    [RelayCommand]
    private void SelectRoutingList(RoutingListSummaryViewModel list)
    {
        _selectedRoutingListId = list.Id;
        var editor = new RoutingListEditorViewModel(_connection, list.Id, list.Name, OnRoutingEditorSaved);
        RoutingEditor = editor;
        UpdateRoutingSelection();
        _ = editor.LoadAsync();
    }

    [RelayCommand]
    private void NewRoutingList()
    {
        _selectedRoutingListId = -1;
        var editor = new RoutingListEditorViewModel(_connection, OnRoutingEditorSaved);
        RoutingEditor = editor;
        UpdateRoutingSelection();
        _ = editor.LoadAsync();
    }

    // The editor auto-saves on change; track the (possibly newly-created) list id so the left-rail
    // selection stays on the row being edited.
    private void OnRoutingEditorSaved(long id)
    {
        _selectedRoutingListId = id;
        UpdateRoutingSelection();
    }

    // Stop a queued auto-save on the editor being replaced/closed so it can't fire after the fact.
    partial void OnRoutingEditorChanged(RoutingListEditorViewModel? oldValue, RoutingListEditorViewModel? newValue)
    {
        oldValue?.CancelPendingSave();
    }

    [RelayCommand]
    private async Task DeleteRoutingListEdit()
    {
        if (RoutingEditor is null)
        {
            return;
        }

        if (await RoutingEditor.DeleteAsync())
        {
            RoutingEditor = null;
            _selectedRoutingListId = -1;
            UpdateRoutingSelection();
        }
    }

    private void UpdateRoutingSelection()
    {
        foreach (var list in RoutingLists)
        {
            list.IsSelected = list.Id == _selectedRoutingListId;
        }
    }

    [RelayCommand]
    private void CloseRoutingEditor()
    {
        RoutingEditor = null;
        _selectedRoutingListId = -1;
        UpdateRoutingSelection();
    }

    [RelayCommand]
    private void SelectRoutingTab(string tab)
    {
        RoutingTab = tab == "sources" ? "sources" : "rules";
    }

    // Infer the source kind from the URL's file name: a name containing "geosite" or "geoip" (any
    // extension) fixes the kind and locks the combo; otherwise the user picks it.
    partial void OnNewSourceUrlChanged(string value)
    {
        var detected = DetectSourceKind(value);
        if (detected is null)
        {
            SourceKindLocked = false;
            return;
        }

        SourceKindLocked = true;
        if (!string.Equals(NewSourceKind, detected, StringComparison.Ordinal))
        {
            NewSourceKind = detected;
        }
    }

    private static string? DetectSourceKind(string url)
    {
        var text = url.Trim().ToLowerInvariant();
        var cut = text.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        var slash = text.LastIndexOf('/');
        var name = slash >= 0 ? text[(slash + 1)..] : text;
        if (name.Contains("geosite", StringComparison.Ordinal))
        {
            return "geosite";
        }

        return name.Contains("geoip", StringComparison.Ordinal) ? "geoip" : null;
    }

    [RelayCommand]
    private async Task AddSource()
    {
        var url = NewSourceUrl.Trim();
        if (url.Length == 0)
        {
            return;
        }

        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpAddSource, [NewSourceKind, url]));
        NewSourceUrl = string.Empty;
    }

    // Per-row update / delete, passed as delegates to each SourceItemViewModel (the delete flyout's
    // popup can't resolve a parent-relative binding back to this view model).
    private Task SendUpdateSourceAsync(SourceItemViewModel source)
    {
        return _connection.SendCommandAsync(new IpcCommand(IpcContract.OpUpdateSource, [source.Name]));
    }

    private Task SendRemoveSourceAsync(SourceItemViewModel source)
    {
        return _connection.SendCommandAsync(new IpcCommand(IpcContract.OpRemoveSource, [source.Name]));
    }

    [RelayCommand]
    private async Task UpdateSources()
    {
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpUpdateSources, []));
    }
}
