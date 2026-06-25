using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
    private string? _pendingOpenProfile;
    private string _updateSetupUrl = string.Empty;
    private string? _bannerUpdateVersion;
    private bool _updateUrlInitialized;
    // Signature (sorted names) of the geo sources that had updates the last time the banner was shown,
    // so a persistent "update available" state isn't re-raised on every snapshot and a dismissed banner
    // stays dismissed until the set of outdated sources changes.
    private string? _geoBannerSignature;

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
    [NotifyPropertyChangedFor(nameof(IsConnectingOut))]
    [NotifyPropertyChangedFor(nameof(IsConnectingIn))]
    [NotifyPropertyChangedFor(nameof(ConnectGlyph))]
    [NotifyPropertyChangedFor(nameof(ConnectHint))]
    [NotifyPropertyChangedFor(nameof(ConnectStageBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleBorderBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleForeground))]
    [NotifyPropertyChangedFor(nameof(ConnectStatusBrush))]
    [NotifyPropertyChangedFor(nameof(TrayStatusColor))]
    private bool _isTunnelActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    [NotifyPropertyChangedFor(nameof(ActiveProfileName))]
    private string? _boundTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    [NotifyPropertyChangedFor(nameof(AgentStatusBrush))]
    [NotifyPropertyChangedFor(nameof(IsConnecting))]
    [NotifyPropertyChangedFor(nameof(IsConnectingOut))]
    [NotifyPropertyChangedFor(nameof(IsConnectingIn))]
    [NotifyPropertyChangedFor(nameof(ConnectGlyph))]
    [NotifyPropertyChangedFor(nameof(ConnectHint))]
    [NotifyPropertyChangedFor(nameof(ConnectStageBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleBorderBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleForeground))]
    [NotifyPropertyChangedFor(nameof(ConnectStatusBrush))]
    [NotifyPropertyChangedFor(nameof(TrayStatusColor))]
    private string _boundStatus = ConnectionStatus.Disconnected;

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
    [NotifyPropertyChangedFor(nameof(IsSettings))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderPower))]
    private string _nav = "home";

    // Profile master-detail: the profile opened for editing (null = the profiles list is shown), and
    // which of its aspect pages the left rail has selected.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProfileList))]
    [NotifyPropertyChangedFor(nameof(IsProfileDetail))]
    [NotifyPropertyChangedFor(nameof(OpenProfileName))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderPower))]
    [NotifyPropertyChangedFor(nameof(ShowProfileAspects))]
    private BalancerItemViewModel? _openProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAspectConfig))]
    [NotifyPropertyChangedFor(nameof(IsAspectRouting))]
    [NotifyPropertyChangedFor(nameof(IsAspectExclusions))]
    [NotifyPropertyChangedFor(nameof(IsAspectProxy))]
    [NotifyPropertyChangedFor(nameof(IsAspectDns))]
    [NotifyPropertyChangedFor(nameof(IsAspectName))]
    private string _profileAspect = "config";

    // Config management (one level below a profile's Конфигурация aspect): the member config opened for
    // actions (null = the member list is shown). The right pane shows one full page with every action
    // (edit / export / location / delete); the left aspect rail stays put and a back control in the right
    // pane returns to the member list.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfigManage))]
    [NotifyPropertyChangedFor(nameof(ShowProfileAspects))]
    [NotifyPropertyChangedFor(nameof(ConfigFilePath))]
    private string? _openConfig;

    // The config page's view model, built when a config is opened. It reuses the export dialog VM, which
    // now also hosts inline editing of the .conf text (the separate editor section was dropped).
    [ObservableProperty]
    private ExportDialogViewModel? _configExport;

    // The open config's WebSocket (UDP-over-TCP) transport settings, shown on its management page.
    [ObservableProperty]
    private ConfigTransportViewModel? _configTransport;

    // The open config's preferred-DNS editor, shown on the profile's DNS aspect (per-config, moved off the
    // former global setting). Built when a config opens, alongside ConfigTransport.
    [ObservableProperty]
    private ConfigDnsViewModel? _configDns;

    // The open config's bypass-exclusions editor, shown on the profile's Маршрутизация aspect (per-config,
    // moved off the former global settings). Built when a config opens.
    [ObservableProperty]
    private ConfigExclusionsViewModel? _configExclusions;

    [ObservableProperty]
    private string _configDeleteStatus = string.Empty;

    // The editable name field for the open config (seeded with its current name) and a one-line status
    // for a rejected rename (e.g. the name is taken, or the config is in use by the running tunnel).
    [ObservableProperty]
    private string _configRename = string.Empty;

    [ObservableProperty]
    private string _configRenameStatus = string.Empty;

    // The editable name field for the open profile (seeded with its current name) and its rename status.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveProfileName))]
    private string _profileRename = string.Empty;

    [ObservableProperty]
    private string _profileRenameStatus = string.Empty;

    // One-line status for the profile export/import (the Импорт/экспорт aspect): "скопировано",
    // "импортирован <name>", or an error from the agent.
    [ObservableProperty]
    private string _profilePortStatus = string.Empty;

    // Inline name-edit mode for the profile header: while true the name shows as a text box with a save
    // (diskette) button; otherwise as text with a pencil button.
    [ObservableProperty]
    private bool _isEditingProfileName;

    // Which settings section the left rail has selected while on the Settings tab.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsGeneral))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSources))]
    [NotifyPropertyChangedFor(nameof(IsSettingsLogs))]
    [NotifyPropertyChangedFor(nameof(IsSettingsAbout))]
    private string _settingsSection = "general";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeLabel))]
    private bool _isDark;

    [ObservableProperty]
    private bool _noticeVisible;

    [ObservableProperty]
    private string? _noticeText;

    // The rule editor for the OPEN profile's selected routing list, shown inline under the profile's
    // Маршрутизация aspect. Driven by the open profile's SelectedRoutingList (built/rebuilt by
    // SyncProfileRoutingEditor); null when no real list is selected.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoutingEditor))]
    [NotifyPropertyChangedFor(nameof(ShowRoutingEditor))]
    [NotifyPropertyChangedFor(nameof(CanEditRouting))]
    private RoutingListEditorViewModel? _routingEditor;

    // The routing page is collapsed to just the picker by default; the rule editor opens on "Редактировать"
    // (an existing list) or by picking "+ Новый список" (a new one).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRoutingEditor))]
    [NotifyPropertyChangedFor(nameof(CanEditRouting))]
    private bool _isRoutingEditing;

    // App self-update (#54): the configured metadata URL, the latest check result, and download state.
    [ObservableProperty]
    private string _updateUrl = string.Empty;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _updateVersion = string.Empty;

    [ObservableProperty]
    private string _updateDescription = string.Empty;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    private bool _updateDownloading;

    [ObservableProperty]
    private int _updateDownloadPercent;

    [ObservableProperty]
    private bool _updateBannerVisible;

    [ObservableProperty]
    private bool _geoUpdateBannerVisible;

    [ObservableProperty]
    private int _geoUpdateCount;

    [ObservableProperty]
    private bool _geoAutoCheck = true;


    [ObservableProperty]
    private int _geoCheckIntervalHours = 24;

    /// <summary>Preset interval options (hours) for the geo auto-check combo; an out-of-band value is
    /// inserted on demand so the combo can always display the agent's actual setting.</summary>
    public ObservableCollection<int> GeoCheckIntervals { get; } = [6, 12, 24, 48, 168];

    [ObservableProperty]
    private string _appVersion = "AmneziaGeo -";

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
    // the view is a single (selectable) text block - no per-line controls to regenerate each push.
    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private bool _hasLogs;

    // The most recent agent log lines (oldest first) as delivered by the last snapshot, kept raw so the
    // severity filter can re-derive LogText without another round-trip.
    private IReadOnlyList<string> _logLines = [];

    // Minimum severity shown in the journal: 0 = все, 1 = INFO и выше, 2 = WARN и выше, 3 = только ошибки.
    // Lines are rendered "HH:mm:ss LVL message"; the 3-char level token drives the filter.
    [ObservableProperty]
    private int _logSeverity;

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
    public string ActiveProfileName => BoundTarget ?? "-";

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
    // while the balancer status still lags at its previous terminal value ("disconnected") for one push -
    // without this bridge that frame snaps the circle back to the off look before "connecting" arrives.
    // Intent on + a down status reads "connecting"; intent off + an up status reads "disconnecting".
    private int ConnState => BoundStatus switch
    {
        ConnectionStatus.Connected => IsTunnelActive ? 2 : 1,
        ConnectionStatus.Connecting or ConnectionStatus.Disconnecting => 1,
        _ => IsTunnelActive ? 1 : 0,
    };

    /// <summary>Whether the connecting spinner shows in the power circle.</summary>
    public bool IsConnecting => ConnState == 1;

    /// <summary>Transient connect (intent on): the power-button ring wave travels outward.</summary>
    public bool IsConnectingOut => IsConnecting && IsTunnelActive;

    /// <summary>Transient disconnect (intent off): the power-button ring wave travels inward.</summary>
    public bool IsConnectingIn => IsConnecting && !IsTunnelActive;

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

    /// <summary>Disc colour for the tray icon: green connected, amber connecting/transient, grey off.
    /// Single source for the tray tint so it tracks the on-screen power control's three states.</summary>
    public Color TrayStatusColor => ConnState switch
    {
        2 => Color.FromRgb(0x1F, 0x9D, 0x57),
        1 => Color.FromRgb(0xE0, 0x90, 0x2F),
        _ => Color.FromRgb(0x7B, 0x81, 0x8D),
    };

    /// <summary>
    /// Whether the Home (profiles) view is shown.
    /// </summary>
    public bool IsHome => Nav == "home";

    /// <summary>
    /// Whether the Settings view is shown (opened via the gear button).
    /// </summary>
    public bool IsSettings => Nav == "settings";

    /// <summary>Whether the profiles list is shown (no profile opened for detail).</summary>
    public bool IsProfileList => OpenProfile is null;

    /// <summary>
    /// Whether the header power control is shown. Hidden on the Home profiles-list, where the large
    /// power button in the right pane takes over (the header control would only duplicate it); shown
    /// on a profile's detail and on the Routing / Settings tabs.
    /// </summary>
    public bool ShowHeaderPower => !(IsHome && IsProfileList);

    /// <summary>Whether a profile is opened: the left rail shows its aspects, the right pane the editor.</summary>
    public bool IsProfileDetail => OpenProfile is not null;

    /// <summary>Name of the opened profile, shown atop its aspect rail.</summary>
    public string OpenProfileName => OpenProfile?.Name ?? string.Empty;

    /// <summary>Whether the inline name editor can save (the typed name is not blank).</summary>
    public bool CanSaveProfileName => !string.IsNullOrWhiteSpace(ProfileRename);

    /// <summary>Whether the opened profile's configuration aspect is selected.</summary>
    public bool IsAspectConfig => ProfileAspect == "config";

    /// <summary>Whether the opened profile's routing aspect is selected.</summary>
    public bool IsAspectRouting => ProfileAspect == "routing";

    /// <summary>Whether the opened profile's exclusions (direct-access) aspect is selected.</summary>
    public bool IsAspectExclusions => ProfileAspect == "exclusions";

    /// <summary>Whether the opened profile's proxy (WebSocket) aspect is selected.</summary>
    public bool IsAspectProxy => ProfileAspect == "proxy";

    /// <summary>Whether the opened profile's DNS aspect is selected.</summary>
    public bool IsAspectDns => ProfileAspect == "dns";

    /// <summary>Whether the opened profile's name aspect is selected.</summary>
    public bool IsAspectName => ProfileAspect == "name";

    /// <summary>Whether a member config is opened for management (the full config page shows on the right).</summary>
    public bool IsConfigManage => OpenConfig is not null;

    /// <summary>
    /// Whether the profile's aspect editors (right pane) show: whenever a profile is open. Each aspect
    /// renders a slice of the profile's single configuration (text / proxy) or its routing / name.
    /// </summary>
    public bool ShowProfileAspects => IsProfileDetail;

    /// <summary>Full path to the opened config's .conf file (shown in the location section).</summary>
    public string ConfigFilePath => OpenConfig is null ? string.Empty : ConfigPaths.ConfigFile(OpenConfig);

    /// <summary>Whether the General settings section is selected.</summary>
    public bool IsSettingsGeneral => SettingsSection == "general";

    /// <summary>Whether the About settings section is selected.</summary>
    public bool IsSettingsAbout => SettingsSection == "about";

    /// <summary>Whether the Logs settings section is selected (the agent journal lives here now).</summary>
    public bool IsSettingsLogs => SettingsSection == "logs";

    /// <summary>Whether the Sources settings section is selected (the geo-data bases live here now).</summary>
    public bool IsSettingsSources => SettingsSection == "sources";

    /// <summary>
    /// Whether the inline rule editor is shown under the open profile's Маршрутизация aspect (a real
    /// or freshly-created list is selected).
    /// </summary>
    public bool HasRoutingEditor => RoutingEditor is not null;

    /// <summary>Whether the inline rule editor is shown - it is open for editing, or it is a brand-new list.</summary>
    public bool ShowRoutingEditor => RoutingEditor is not null && (IsRoutingEditing || RoutingEditor.IsNew);

    /// <summary>Whether to offer "Редактировать": a saved list is selected and the editor is still collapsed.</summary>
    public bool CanEditRouting => RoutingEditor is not null && !RoutingEditor.IsNew && !IsRoutingEditing;

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
    private void NavSettings()
    {
        Nav = "settings";
    }

    [RelayCommand]
    private void SelectSettings(string section)
    {
        SettingsSection = section;
    }

    // Open a profile into its detail view (aspect rail + right-pane editor), starting on the overview.
    [RelayCommand]
    private void OpenProfileDetail(BalancerItemViewModel profile)
    {
        OpenProfile = profile;
        ProfileAspect = "config";
    }

    // Header back control (one button, always labelled "Назад"): from the config page back to the member
    // list, otherwise from a profile's detail back to the profiles list.
    [RelayCommand]
    private void Back()
    {
        OpenProfile = null;
    }

    // Switch which aspect page (overview / config / routing / balancer / name) the right pane shows. A rail
    // click always returns to the aspect's list (never resumes a config that was open for management),
    // even when the same aspect is re-selected - so coming back never lands mid-edit.
    [RelayCommand]
    private void SelectProfileAspect(string aspect)
    {
        ProfileAspect = aspect;
    }

    // Open a member config into its full management page (right pane). The aspect rail stays put.
    [RelayCommand]
    private void OpenConfigManage(string configName)
    {
        OpenConfig = configName;
    }

    // Reveal the opened config's .conf in Explorer (the location action's button).
    [RelayCommand]
    private void RevealConfig()
    {
        if (OpenConfig is not null)
        {
            ConfigPaths.RevealInExplorer(OpenConfig);
        }
    }

    // Delete the opened config from the catalogue (and the open profile's members), then return to the
    // config list. The agent refuses while the config is in use by the running profile, so on a non-OK
    // ack the view stays put and shows why.
    [RelayCommand]
    private async Task DeleteOpenConfig()
    {
        if (OpenConfig is null || OpenProfile is null)
        {
            return;
        }

        ConfigDeleteStatus = string.Empty;
        var ack = await OpenProfile.DeleteConfigAsync(OpenConfig);
        if (ack.Ok)
        {
            OpenConfig = null;
        }
        else
        {
            ConfigDeleteStatus = ack.Message;
        }
    }

    // Rename the open config. On success the page closes; the rebroadcast list shows the new name, under
    // which the user can reopen it. On a refused rename (name taken, or in use by the running tunnel) the
    // page stays put and shows why.
    [RelayCommand]
    private async Task RenameConfig()
    {
        var current = OpenConfig;
        if (current is null)
        {
            return;
        }

        var next = (ConfigRename ?? string.Empty).Trim();
        if (next.Length == 0 || string.Equals(next, current, StringComparison.Ordinal))
        {
            return;
        }

        ConfigRenameStatus = string.Empty;
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpRenameConfig, [current, next]));
        if (ack.Ok)
        {
            OpenConfig = null;
        }
        else
        {
            ConfigRenameStatus = ack.Message;
        }
    }

    // Build the config page's view model when a config is opened; null it out when the page closes.
    partial void OnOpenConfigChanged(string? value)
    {
        ConfigDeleteStatus = string.Empty;
        ConfigRename = value ?? string.Empty;
        ConfigRenameStatus = string.Empty;
        if (value is null)
        {
            ConfigExport = null;
            ConfigTransport = null;
            ConfigDns = null;
            ConfigExclusions = null;
            return;
        }

        var export = new ExportDialogViewModel(_connection, value);
        ConfigExport = export;
        _ = export.LoadAsync();

        // Seed the WebSocket (UDP-over-TCP) transport, preferred-DNS, and bypass-exclusions editors from the
        // opened config's current snapshot values.
        var item = Configs.FirstOrDefault(c => string.Equals(c.Name, value, StringComparison.Ordinal));
        ConfigTransport = new ConfigTransportViewModel(_connection, value, item?.Endpoint ?? string.Empty, item?.UseWebSocket ?? false, item?.WebSocketHost ?? string.Empty, item?.WebSocketPort ?? 443);
        ConfigDns = new ConfigDnsViewModel(_connection, value, item?.Dns ?? string.Empty);
        ConfigExclusions = new ConfigExclusionsViewModel(_connection, value, item?.Exclusions ?? string.Empty);
    }

    // Track the open profile so its SelectedRoutingList drives the inline rule editor. Subscribing to the
    // instance is safe because the snapshot reconcile keeps the same BalancerItemViewModel instances.
    partial void OnOpenProfileChanged(BalancerItemViewModel? oldValue, BalancerItemViewModel? newValue)
    {
        // Open the profile's single configuration so its editors (text / proxy) are available on the rail.
        OpenConfig = string.IsNullOrEmpty(newValue?.Config) ? null : newValue!.Config;

        // Reset the inline rename editor only when the profile identity actually changes. A background
        // snapshot can re-assign the open profile to a refreshed instance of the SAME profile; without this
        // guard that would wipe the half-typed name and close the editor mid-edit (the reported focus loss).
        var sameProfile = oldValue is not null && newValue is not null
            && string.Equals(oldValue.Name, newValue.Name, StringComparison.Ordinal);
        if (!sameProfile)
        {
            ProfileRename = newValue?.Name ?? string.Empty;
            ProfileRenameStatus = string.Empty;
            IsEditingProfileName = false;
        }
        ProfilePortStatus = string.Empty;

        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnOpenProfilePropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnOpenProfilePropertyChanged;
        }
        else
        {
            RoutingEditor = null;
        }

        SyncProfileRoutingEditor();
    }

    private void OnOpenProfilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BalancerItemViewModel.SelectedRoutingList))
        {
            SyncProfileRoutingEditor();
        }
        else if (e.PropertyName == nameof(BalancerItemViewModel.Config))
        {
            OpenConfig = string.IsNullOrEmpty(OpenProfile?.Config) ? null : OpenProfile!.Config;
        }
    }

    partial void OnProfileAspectChanged(string value)
    {
        if (value == "routing")
        {
            SyncProfileRoutingEditor();
        }
    }

    // Build (or keep) the inline rule editor for the open profile's selected routing list. Only runs on
    // the Маршрутизация aspect so opening a profile does not fetch rules the user may never look at.
    private void SyncProfileRoutingEditor()
    {
        var profile = OpenProfile;
        if (profile is null || ProfileAspect != "routing")
        {
            return;
        }

        var choice = profile.SelectedRoutingList;
        if (choice is null || choice.IsNone)
        {
            RoutingEditor = null;
            return;
        }

        if (choice.IsNewSentinel)
        {
            // Picking "+ Новый список": show a fresh create-editor (keep the one in progress, don't rebuild).
            if (RoutingEditor is not { IsNew: true })
            {
                var editor = new RoutingListEditorViewModel(_connection, OnProfileRoutingEditorSaved);
                RoutingEditor = editor;
                _ = editor.LoadAsync();
            }

            return;
        }

        // A real, existing list: build its editor unless it is already the one being shown.
        if (RoutingEditor is null || RoutingEditor.Id != choice.Id)
        {
            var editor = new RoutingListEditorViewModel(_connection, choice.Id!.Value, choice.Name, OnProfileRoutingEditorSaved);
            RoutingEditor = editor;
            IsRoutingEditing = false;
            _ = editor.LoadAsync();
        }
    }

    // When a freshly-created inline list is first saved (gets a real id), bind it to the open profile so
    // the profile starts using it; the next snapshot resolves SelectedRoutingList to the new list.
    private void OnProfileRoutingEditorSaved(long id)
    {
        if (OpenProfile is { SelectedRoutingList.IsNewSentinel: true } profile)
        {
            // The new list now has a real id: record it so the "+ Новый список" hold releases to this list
            // (not the previously-assigned one) on the next snapshot, then bind it to the profile.
            profile.NotifyNewListSaved(id);
            _ = AssignRoutingAsync(profile.Name, id, false);
        }
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
            // Drop the open profile too: its BalancerItemViewModel is about to be cleared, and the Home
            // detail pane would otherwise keep editing a vanished profile (firing IPC at a dead pipe)
            // until the next reconnect snapshot rebuilds the list.
            OpenProfile = null;
            ProfileAspect = "config";
            _configNames = [];
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

        // The agent stores the selected/bound target as EITHER a profile name or the bare config name the
        // profile wraps (a legacy `set-profile <config>`, a preconfigured "main" seed, or a target set out
        // of band). A profile's name and its config name never coincide - they share one namespace - so we
        // match on either; otherwise the current target lights up no row at all (the reported bug).
        var selected = snapshot.SelectedTarget ?? snapshot.BoundTarget;
        foreach (var item in Balancers)
        {
            item.IsActive = ProfileMatchesTarget(item, selected);
            // A DIFFERENT profile is the live tunnel: this profile's connect button reads "Переключить".
            item.OtherActive = snapshot.Active && !ProfileMatchesTarget(item, snapshot.BoundTarget);
        }

        // Top-center notice (auto-hides after 5s, dismissable): a different profile is selected while a
        // tunnel is up (reconnect to apply - no auto-switch), settings changed on a live tunnel, or a
        // better member is available on a backup. Shown once per distinct notice, not re-armed while
        // the same one holds.
        string? notice = null;
        if (snapshot.ConnectFailed)
        {
            // Distinguish a memberless profile (nothing to dial) from a real reachability failure, so the
            // message is actionable instead of the misleading "сервер не ответил".
            var emptyProfile = snapshot.SelectedTarget is not null
                && snapshot.Balancers.FirstOrDefault(b =>
                       string.Equals(b.Name, snapshot.SelectedTarget, StringComparison.Ordinal)) is { Config.Length: 0 };
            notice = emptyProfile
                ? $"Профиль «{snapshot.SelectedTarget}» пуст - добавьте конфигурацию."
                : "Не удалось подключиться - сервер не ответил.";
        }
        else if (snapshot.Active && SelectedDiffersFromBound(snapshot))
        {
            notice = $"Выбран профиль «{snapshot.SelectedTarget}». Переподключитесь, чтобы применить.";
        }
        else if (snapshot.RestartRequired)
        {
            notice = "Настройки изменены. Переподключитесь, чтобы применить.";
        }

        ShowNotice(notice);

        _suppressSettingPush = true;
        GeoAutoCheck = snapshot.GeoAutoCheck;
        EnsureGeoInterval(snapshot.GeoCheckIntervalHours);
        GeoCheckIntervalHours = snapshot.GeoCheckIntervalHours;
        _suppressSettingPush = false;

        ApplyUpdateState(snapshot);
        ApplyGeoUpdateBanner();

        _logLines = snapshot.Logs ?? [];
        HasLogs = _logLines.Count > 0;
        RebuildLogText();
    }

    // A profile "is" the given target when the target equals its name or the config it wraps. The agent's
    // selected/bound target can be stored as either form, so the UI resolves both to the same profile row.
    private static bool ProfileMatchesTarget(BalancerItemViewModel item, string? target)
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

        var selectedProfile = Balancers.FirstOrDefault(b => ProfileMatchesTarget(b, snapshot.SelectedTarget));
        var boundProfile = Balancers.FirstOrDefault(b => ProfileMatchesTarget(b, snapshot.BoundTarget));
        return !(selectedProfile is not null && ReferenceEquals(selectedProfile, boundProfile));
    }

    partial void OnLogSeverityChanged(int value)
    {
        RebuildLogText();
    }

    // Rebuilds the journal text from the raw lines applying the current severity filter, newest first so
    // the latest activity stays visible at the top without scrolling.
    private void RebuildLogText()
    {
        var threshold = LogSeverity;
        var shown = threshold <= 0
            ? _logLines
            : [.. _logLines.Where(line => LineRank(line) >= threshold)];
        LogText = shown.Count == 0 ? string.Empty : string.Join('\n', shown.Reverse());
    }

    // Severity rank of a rendered log line ("HH:mm:ss LVL message"): trace/debug = 0, info = 1,
    // warn = 2, error/fatal = 3. Unparseable lines rank as info so they are never hidden by a relaxed
    // filter yet drop out under the errors-only view.
    private static int LineRank(string line)
    {
        if (line.Length < 12)
        {
            return 1;
        }

        return line.Substring(9, 3) switch
        {
            "TRC" or "DBG" => 0,
            "WRN" => 2,
            "ERR" or "FTL" => 3,
            _ => 1,
        };
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

    partial void OnGeoAutoCheckChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync("geo-auto-check", value);
        }
    }

    partial void OnGeoCheckIntervalHoursChanged(int value)
    {
        if (!_suppressSettingPush && value > 0)
        {
            _ = _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting,
                ["geo-check-interval-hours", value.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
        }
    }

    // Keeps the interval combo able to display whatever the agent reports: an out-of-band value (e.g. set
    // via CLI) that isn't a preset is inserted in order, so the ComboBox SelectedItem never goes null -
    // which, two-way-bound to an int, would otherwise write 0 back into the property.
    private void EnsureGeoInterval(int hours)
    {
        if (hours <= 0 || GeoCheckIntervals.Contains(hours))
        {
            return;
        }

        var index = 0;
        while (index < GeoCheckIntervals.Count && GeoCheckIntervals[index] < hours)
        {
            index++;
        }

        GeoCheckIntervals.Insert(index, hours);
    }

    private async Task SetSettingAsync(string key, bool value)
    {
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting, [key, value ? "on" : "off"]));
    }

    // Applies the update-related snapshot fields. The URL field is initialised once then left to the
    // user (saved on «Проверить») so periodic snapshots do not clobber typing; a freshly available
    // version raises the banner once.
    private void ApplyUpdateState(StatusSnapshot snapshot)
    {
        AppVersion = $"AmneziaGeo {(string.IsNullOrEmpty(snapshot.AgentVersion) ? "-" : snapshot.AgentVersion)}";

        if (!_updateUrlInitialized)
        {
            UpdateUrl = snapshot.UpdateUrl;
            _updateUrlInitialized = true;
        }

        UpdateAvailable = snapshot.UpdateAvailable;
        UpdateVersion = snapshot.UpdateVersion;
        UpdateDescription = snapshot.UpdateDescription;
        _updateSetupUrl = snapshot.UpdateSetupUrl;

        if (snapshot.UpdateAvailable && !string.IsNullOrEmpty(snapshot.UpdateVersion))
        {
            if (!string.Equals(snapshot.UpdateVersion, _bannerUpdateVersion, StringComparison.Ordinal))
            {
                _bannerUpdateVersion = snapshot.UpdateVersion;
                UpdateBannerVisible = true;
            }
        }
        else
        {
            UpdateBannerVisible = false;
            _bannerUpdateVersion = null;
        }
    }


    [RelayCommand]
    private async Task CheckUpdate()
    {
        UpdateStatus = "Проверка…";
        // Save the URL first (so the agent and the periodic check use it), then ask for a check.
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting, ["update-url", UpdateUrl ?? string.Empty]));
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpCheckUpdate, []));
        UpdateStatus = ack.Message;
    }

    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (string.IsNullOrEmpty(_updateSetupUrl) || UpdateDownloading)
        {
            return;
        }

        UpdateBannerVisible = false;
        UpdateDownloading = true;
        UpdateDownloadPercent = 0;
        UpdateStatus = "Загрузка установщика…";
        try
        {
            var path = await DownloadSetupAsync(_updateSetupUrl, new Progress<int>(p => UpdateDownloadPercent = p));
            UpdateStatus = "Запуск установщика…";

            // /passive: a single progress UI, no prompts. The display level propagates to the upgrade's
            // related-bundle uninstall, so the old version is removed WITHOUT its own second installer
            // window flashing alongside the new one. UseShellExecute lets the bundle elevate (UAC) once.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Arguments = "/passive" });

            // Quit so the installer can replace the app's in-use files.
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Ошибка: {ex.Message}";
        }
        finally
        {
            UpdateDownloading = false;
        }
    }

    [RelayCommand]
    private void DismissUpdateBanner()
    {
        UpdateBannerVisible = false;
    }

    // Raises the geo-list update banner once per "wave": when the set of sources with a pending update
    // changes to a non-empty set the banner shows; a dismissed banner stays dismissed until that set
    // changes again; when nothing is outdated the banner hides. Driven off the per-source flags the
    // snapshot already carries, so no extra round-trip is needed.
    private void ApplyGeoUpdateBanner()
    {
        var outdated = Sources
            .Where(s => s.UpdateAvailable)
            .Select(s => s.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        GeoUpdateCount = outdated.Count;
        if (outdated.Count == 0)
        {
            GeoUpdateBannerVisible = false;
            _geoBannerSignature = null;
            return;
        }

        var signature = string.Join('\n', outdated);
        if (!string.Equals(signature, _geoBannerSignature, StringComparison.Ordinal))
        {
            _geoBannerSignature = signature;
            GeoUpdateBannerVisible = true;
        }
    }

    [RelayCommand]
    private async Task UpdateGeoNow()
    {
        GeoUpdateBannerVisible = false;
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpUpdateSources, []));
    }

    [RelayCommand]
    private void DismissGeoUpdateBanner()
    {
        GeoUpdateBannerVisible = false;
    }

    // Streams the installer to a temp file, reporting integer download percent (mirrors the agent's
    // GeoFileUpdater loop but writes straight to disk - the setup is ~100 MB).
    private static async Task<string> DownloadSetupAsync(string url, IProgress<int> progress)
    {
        var path = Path.Combine(Path.GetTempPath(), "AmneziaGeoSetup.exe");
        using var http = new HttpClient();
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var file = File.Create(path);
        var buffer = new byte[81920];
        long read = 0;
        var lastPercent = -1;
        int n;
        while ((n = await source.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            if (total is > 0)
            {
                var percent = (int)(read * 100 / total.Value);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress.Report(percent);
                }
            }
        }

        return path;
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
            existing.UseWebSocket = entry.WebSocket;
            existing.WebSocketHost = entry.WebSocketHost;
            existing.WebSocketPort = entry.WebSocketPort;
            existing.Dns = entry.Dns;
            existing.Exclusions = entry.Exclusions;
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
            existing.UpdateAvailable = entry.UpdateAvailable;
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
                existing = new BalancerItemViewModel(SaveBalancerAsync, AssignRoutingAsync, SelectProfileAsync, ImportConfigAsync, ToggleProfileConnectionAsync, RemoveConfigAsync);
                existing.ApplyFromEntry(entry, options);
                Balancers.Insert(Math.Min(i, Balancers.Count), existing);
                continue;
            }

            existing.ApplyFromEntry(entry, options);
            var index = Balancers.IndexOf(existing);
            if (index != i)
            {
                Balancers.Move(index, i);
            }
        }

        // If the profile opened in the detail view was removed elsewhere, fall back to the list.
        if (OpenProfile is not null && !Balancers.Contains(OpenProfile))
        {
            OpenProfile = null;
        }

        // Open a profile just created via "+ Профиль" straight into its detail (on the configuration
        // aspect) so configs can be added immediately.
        if (_pendingOpenProfile is not null)
        {
            var created = Balancers.FirstOrDefault(b => string.Equals(b.Name, _pendingOpenProfile, StringComparison.Ordinal));
            if (created is not null)
            {
                OpenProfile = created;
                ProfileAspect = "config";
                _pendingOpenProfile = null;
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

        // The trailing "+ Новый список" sentinel reveals the inline new-list editor, mirroring the
        // "+ Новая конфигурация" sentinel in the config combo.
        options.Add(RoutingListChoice.NewList);
        return options;
    }

    // "+ Профиль": create an empty profile (no dialog), then auto-expand it so configs can be added.
    [RelayCommand]
    private async Task CreateProfile()
    {
        var name = UniqueProfileName();
        var ack = await _connection.SendCommandAsync(
            new IpcCommand(IpcContract.OpAddBalancer, [name]));
        if (ack.Ok)
        {
            _pendingOpenProfile = name;
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

    private async Task SaveBalancerAsync(string name, string config)
    {
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpAddBalancer, [name, config]));
    }

    private async Task<IpcAck> ImportConfigAsync(string name, string confText)
    {
        return await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpImportConfig, [name, confText]));
    }

    private async Task<IpcAck> RemoveConfigAsync(string name)
    {
        return await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpRemoveConfig, [name]));
    }

    // Delete the profile currently open in the detail view, then fall back to the profiles list. The
    // agent refuses while the profile is the running tunnel, so on a non-OK ack the view stays put.
    [RelayCommand]
    private async Task DeleteOpenProfile()
    {
        if (OpenProfile is null)
        {
            return;
        }

        var ack = await _connection.SendCommandAsync(
            new IpcCommand(IpcContract.OpRemoveBalancer, [OpenProfile.Name]));
        if (ack.Ok)
        {
            OpenProfile = null;
        }
    }

    // Rename the open profile. On success the detail closes; the rebroadcast list shows the new name. On a
    // "✎" in the profile header: switch the name to an inline editor seeded with the current name.
    [RelayCommand]
    private void BeginProfileNameEdit()
    {
        ProfileRename = OpenProfileName;
        ProfileRenameStatus = string.Empty;
        IsEditingProfileName = true;
    }

    // "✕" next to the inline name editor: leave rename mode without saving, restoring the current name.
    [RelayCommand]
    private void CancelProfileNameEdit()
    {
        ProfileRename = OpenProfileName;
        ProfileRenameStatus = string.Empty;
        IsEditingProfileName = false;
    }

    // refused rename (name taken, or it is the running profile) the view stays put and shows why.
    [RelayCommand]
    private async Task RenameProfile()
    {
        var profile = OpenProfile;
        if (profile is null)
        {
            return;
        }

        var next = (ProfileRename ?? string.Empty).Trim();
        if (next.Length == 0 || string.Equals(next, profile.Name, StringComparison.Ordinal))
        {
            IsEditingProfileName = false;
            return;
        }

        ProfileRenameStatus = string.Empty;
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpRenameProfile, [profile.Name, next]));
        if (ack.Ok)
        {
            OpenProfile = null;
        }
        else
        {
            ProfileRenameStatus = ack.Message;
        }
    }

    // Export the open profile as a portable JSON bundle (config with keys, transport, geo, routing). The
    // window code-behind moves the returned text to the clipboard or a file; null means there was nothing
    // to export (no open profile) or the agent refused (the status carries why).
    public async Task<string?> ExportOpenProfileAsync()
    {
        var profile = OpenProfile;
        if (profile is null)
        {
            return null;
        }

        ProfilePortStatus = string.Empty;
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpExportProfile, [profile.Name]));
        if (!ack.Ok)
        {
            ProfilePortStatus = ack.Message;
            return null;
        }

        return ack.Message;
    }

    // Import a profile from a portable JSON bundle (clipboard or file). Import is offered only from inside an
    // open profile's settings, so the bundle restores *into* that profile - replacing its config, transport,
    // geo, routing and exclusions in place while keeping its name (and thus its connection-target selection).
    // On success the (same) profile re-opens once the next snapshot lands.
    public async Task ImportProfileBundleAsync(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            ProfilePortStatus = "Нет данных для импорта.";
            return;
        }

        var target = OpenProfile?.Name;
        ProfilePortStatus = string.Empty;
        var args = string.IsNullOrEmpty(target) ? new[] { json } : new[] { json, target };
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpImportProfile, args));
        if (ack.Ok)
        {
            _pendingOpenProfile = ack.Message;
            ProfilePortStatus = string.IsNullOrEmpty(target)
                ? $"Профиль импортирован: {ack.Message}"
                : "Профиль восстановлен из файла.";
        }
        else
        {
            ProfilePortStatus = ack.Message;
        }
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

    // Per-profile connect/disconnect from a profile's detail. Connecting first selects the profile, then
    // connects: the agent latches the new target on connect and the supervisor switches a live tunnel to
    // it (tears the old one down, brings this one up). Optimistic state mirrors ToggleConnection so the
    // header power control does not flicker while the switch is in flight.
    private async Task ToggleProfileConnectionAsync(string profile, bool connect)
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

    // "Редактировать": open the inline rule editor for the selected saved list (collapsed by default so the
    // routing page is just the picker until the user opts in).
    [RelayCommand]
    private void BeginRoutingEdit()
    {
        IsRoutingEditing = true;
    }

    // "Закрыть": collapse the inline rule editor back to just the picker. An in-progress NEW list is dropped
    // (a real, saved list stays in the catalogue and reopens via "Редактировать"); nothing persists here, so
    // unsaved edits are discarded - saving is now always explicit (the "Сохранить" button in the editor).
    [RelayCommand]
    private void CloseRoutingEdit()
    {
        IsRoutingEditing = false;
        if (RoutingEditor is { IsNew: true })
        {
            RoutingEditor = null;
        }
    }

    // "Удалить список" in the inline editor: delete the shared list, then clear the open profile's
    // routing. The snapshot resolves SelectedRoutingList back to "none" once the list is gone.
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
            if (OpenProfile is not null)
            {
                await AssignRoutingAsync(OpenProfile.Name, null, false);
            }
        }
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

    [RelayCommand]
    private async Task CheckSources()
    {
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpCheckSources, []));
        ShowNotice(ack.Message);
    }
}
