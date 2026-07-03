using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
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
    // Signature (sorted names) of the geo sources that had updates the last time the banner was shown,
    // so a persistent "update available" state isn't re-raised on every snapshot and a dismissed banner
    // stays dismissed until the set of outdated sources changes.
    private string? _geoBannerSignature;
    // Set while the main-window profile combo is assigned from a snapshot reconcile (Apply), so the
    // programmatic selection does not echo back an OpSelectProfile to the agent. Mirrors the per-row
    // _suppress flags already used in BalancerItemViewModel.
    private bool _suppressActivePush;
    // A routing list just created in the Routing section, whose summary row has not yet arrived in a
    // snapshot. SyncRoutingLists selects it (EditRoutingList) once the row is present, then clears this.
    private long? _pendingEditRoutingListId;
    // A config just imported in the Config section, whose row has not yet arrived in a snapshot.
    // SyncConfigs opens it (OpenConfig) once present, so its transport editor seeds from the real row.
    private string? _pendingOpenConfig;
    // Set while a profile combo's selection is mirrored from state (ActiveProfile / OpenProfile) rather than
    // chosen by the user, so the programmatic assignment does not re-enter the pick handler and echo back.
    private bool _suppressActiveChoice;
    private bool _suppressOpenChoice;
    // Guard the Config/Routing section combos against echoing a pick back while we mirror their state
    // (open config / create form / selected list) into the wrapped selection - same idea as the profile combos.
    private bool _suppressCatalogueConfig;
    private bool _suppressCatalogueRouting;

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
    [NotifyPropertyChangedFor(nameof(ConnectCircleBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleBorderBrush))]
    [NotifyPropertyChangedFor(nameof(ConnectCircleForeground))]
    [NotifyPropertyChangedFor(nameof(ConnectStatusBrush))]
    [NotifyPropertyChangedFor(nameof(TrayStatusColor))]
    private string _boundStatus = ConnectionStatus.Disconnected;

    [ObservableProperty]
    private bool _hasConfigs;

    [ObservableProperty]
    private bool _hasBalancers;

    [ObservableProperty]
    private bool _hasRoutingLists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome))]
    [NotifyPropertyChangedFor(nameof(IsSettings))]
    private string _nav = "home";

    // The profile opened for editing in the Profile settings section (null = none opened).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProfileDetail))]
    private BalancerItemViewModel? _openProfile;

    // The Profile-section combo's selection, wrapped as a ProfileChoice so the combo also offers
    // «— не выбрано —» and «+ Новый профиль». Mirrors OpenProfile; the pick handler translates it.
    [ObservableProperty]
    private ProfileChoice? _openProfileChoice = ProfileChoice.None;

    // The profile chosen in the main-window profile combo: the agent's selected target. Picking one selects
    // it on the agent (OpSelectProfile) and persists its name so the same profile is restored on next launch.
    // Connect is gated on this being non-null. Assigned from the snapshot inside a suppression flag so the
    // reconcile that follows does not echo a redundant select back to the agent.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    private BalancerItemViewModel? _activeProfile;

    // The main-window combo's selection, wrapped as a ProfileChoice (see OpenProfileChoice). Picking
    // «+ Новый профиль» redirects into the Profile section and creates a profile there.
    [ObservableProperty]
    private ProfileChoice? _activeProfileChoice = ProfileChoice.None;

    // The routing list chosen in the Routing settings section for editing. On change the section's rule
    // editor (RoutingEditor) and per-routing settings (RoutingSettings) are (re)built for that list.
    [ObservableProperty]
    private RoutingListSummaryViewModel? _editRoutingList;

    // The Config / Routing section combos' wrapped selection, mirroring the profile combos: «— не выбрано —»,
    // «+ Новая конфигурация» / «+ Новый список», then the saved entries. Picking a sentinel opens the create
    // form / editor in place; a redirect from a profile's picker lands here on the "+ new" item.
    [ObservableProperty]
    private ConfigChoice? _selectedCatalogueConfig = ConfigChoice.None;

    [ObservableProperty]
    private RoutingListChoice? _selectedCatalogueRouting = RoutingListChoice.None;

    // The per-routing traffic editor (local DNS, exclusions, all-UDP) for the routing list open in the
    // Routing settings section; null until a real (saved) list is selected.
    [ObservableProperty]
    private RoutingSettingsViewModel? _routingSettings;

    // Config management (one level below a profile's Конфигурация aspect): the member config opened for
    // actions (null = the member list is shown). The right pane shows one full page with every action
    // (edit / export / location / delete); the left aspect rail stays put and a back control in the right
    // pane returns to the member list.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfigManage))]
    private string? _openConfig;

    // The config page's view model, built when a config is opened. It reuses the export dialog VM, which
    // now also hosts inline editing of the .conf text (the separate editor section was dropped).
    [ObservableProperty]
    private ExportDialogViewModel? _configExport;

    // The open config's WebSocket (UDP-over-TCP) transport settings, shown on its management page.
    [ObservableProperty]
    private ConfigTransportViewModel? _configTransport;

    [ObservableProperty]
    private string _configDeleteStatus = string.Empty;

    // The editable name field for the open config (seeded with its current name) and a one-line status
    // for a rejected rename (e.g. the name is taken, or the config is in use by the running tunnel).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveConfigName))]
    private string _configRename = string.Empty;

    [ObservableProperty]
    private string _configRenameStatus = string.Empty;

    // Inline name-edit mode for the config header (mirrors the profile header): while true the name shows
    // as a text box with save/cancel buttons; otherwise as text with a pencil.
    [ObservableProperty]
    private bool _isEditingConfigName;

    // The editable name field for the open profile (seeded with its current name) and its rename status.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveProfileName))]
    private string _profileRename = string.Empty;

    [ObservableProperty]
    private string _profileRenameStatus = string.Empty;

    // Which settings section the section rail has selected on the Settings surface.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsProfile))]
    [NotifyPropertyChangedFor(nameof(IsSettingsConfig))]
    [NotifyPropertyChangedFor(nameof(IsSettingsRouting))]
    [NotifyPropertyChangedFor(nameof(IsSettingsGeneral))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSources))]
    [NotifyPropertyChangedFor(nameof(IsSettingsLogs))]
    private string _settingsSection = "profile";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeLabel))]
    private bool _isDark;

    [ObservableProperty]
    private bool _noticeVisible;

    [ObservableProperty]
    private string? _noticeText;

    // The rule editor for the routing list open in the Routing settings section; null when none is selected.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoutingEditor))]
    private RoutingListEditorViewModel? _routingEditor;

    // App self-update (#54): the metadata URL (baked into the build, surfaced read-only via the snapshot to
    // gate the update UI), the latest check result, and download state.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateUrl))]
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

    // Standalone "+ Новая конфигурация" import form on the Config settings section: adds a config to the
    // shared catalogue without going through a profile. Mirrors the per-profile inline form, but the import
    // is dispatched straight to the catalogue (ImportConfigAsync) rather than assigned to any profile.
    [ObservableProperty]
    private bool _isCreatingSectionConfig;

    [ObservableProperty]
    private string _sectionConfigName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveSectionConfig))]
    private string _sectionConfigText = string.Empty;

    [ObservableProperty]
    private string _sectionConfigStatus = string.Empty;


    [ObservableProperty]
    private int _geoCheckIntervalHours = 24;

    /// <summary>Preset interval options (hours) for the geo auto-check combo; an out-of-band value is
    /// inserted on demand so the combo can always display the agent's actual setting.</summary>
    public ObservableCollection<int> GeoCheckIntervals { get; } = [6, 12, 24, 48, 168];

    [ObservableProperty]
    private int _geoCacheValidityHours = 24;

    /// <summary>Preset options (hours) for how long the geo address cache stays current before a background
    /// refresh re-validates it (#83); an out-of-band value is inserted so the combo always shows the agent's
    /// actual setting.</summary>
    public ObservableCollection<int> GeoCacheValidities { get; } = [6, 12, 24, 48, 72, 168];

    /// <summary>Log verbosity options shown in settings (#82). "Обычный" is the default; "Трасса" captures
    /// every connect step and timing for support diagnosis.</summary>
    public ObservableCollection<string> LogLevels { get; } = [Loc.Instance.Get("MainVm_LogLevelNormal"), Loc.Instance.Get("MainVm_LogLevelDebug"), Loc.Instance.Get("MainVm_LogLevelTrace")];

    // Selected verbosity label; two-way bound to the combo. Mapped to/from the persisted token (info/debug/
    // trace) so raising it writes a live set-setting the agent and tunnel apply without a reconnect.
    [ObservableProperty]
    private string _logLevelLabel = Loc.Instance.Get("MainVm_LogLevelNormal");

    // The dedicated routing log toggle (#82): two-way bound to a switch in the logs settings. When on, every
    // route/resolve is appended to routes.log (included in the diagnostics bundle) for support diagnosis of a
    // "not routed / slow to load" report. Applied live in both processes; off by default.
    [ObservableProperty]
    private bool _routeLogEnabled;

    /// <summary>UI language options for the settings combo (#106): System / Русский / English. Index 0's label
    /// ("Системный"/"System") is localized; the language names stay in their own script.</summary>
    public ObservableCollection<string> Languages { get; } = [Loc.Instance.Get("Lang_System"), "Русский", "English"];

    // Selected language as a combo index: 0 = follow system, 1 = Russian, 2 = English. Two-way bound; a change
    // persists the token to ui-prefs.json and switches the UI culture live (#106).
    [ObservableProperty]
    private int _selectedLanguageIndex;

    [ObservableProperty]
    private string _appVersion = "AmneziaGeo -";

    // AmneziaWG engine (tunnel.dll) version reported by the agent; "н/д" until known / if unresolved.
    [ObservableProperty]
    private string _amneziaVersion = Loc.Instance.Get("MainVm_NotAvailable");

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

    // Signature of the geo category surface (each source's name + its category count) from the last
    // snapshot. When it changes - a source finished downloading, was added or removed - the open routing
    // editor's category suggestions are refreshed, so newly added geo data shows up in the rule search
    // without reopening the editor (previously it only appeared after an app restart).
    private string _geoCategorySignature = string.Empty;

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

    // Persisted per-user UI preferences (#51): theme + window size + splitter width + settings section.
    private readonly UiPreferences _prefs;

    /// <summary>
    /// ctor
    /// </summary>
    public MainWindowViewModel(AgentConnection connection, UiPreferences prefs)
    {
        _connection = connection;
        _prefs = prefs;
        // Seed the persisted theme flag, settings section, and language index. Assign the backing fields
        // directly so initialising from prefs does not echo back as a redundant save / culture re-apply.
        _isDark = prefs.IsDark;
        _settingsSection = prefs.SettingsSection;
        _selectedLanguageIndex = IndexForLanguage(prefs.Language);
        // Re-read code-computed labels (and the "System" combo entry) when the language switches live (#106).
        Loc.Instance.CultureChanged += OnCultureChanged;
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
    /// The same profile rows under the name the new shell uses (a profile = a reusable config × routing
    /// pair). Aliases <see cref="Balancers"/> so the main-window profile combo and the Profile settings
    /// section bind to one collection without duplicating the reconcile.
    /// </summary>
    public ObservableCollection<BalancerItemViewModel> Profiles => Balancers;

    /// <summary>
    /// The options shown in both profile combos: «— не выбрано —», «+ Новый профиль», then every saved
    /// profile by name. Reconciled in place from <see cref="Balancers"/> so a snapshot push does not null
    /// the combos' selection.
    /// </summary>
    public ObservableCollection<ProfileChoice> ProfileOptions { get; } = [ProfileChoice.None, ProfileChoice.New];

    /// <summary>
    /// Config-section catalogue combo: «— не выбрано —», «+ Новая конфигурация», then the saved configs.
    /// Reconciled in place from <see cref="Configs"/> so a snapshot push does not null the combo's selection.
    /// </summary>
    public ObservableCollection<ConfigChoice> ConfigCatalogueOptions { get; } = [ConfigChoice.None, ConfigChoice.NewConfig];

    /// <summary>
    /// Routing-section catalogue combo: «— не выбрано —», «+ Новый список», then the saved lists. Reconciled
    /// in place from <see cref="RoutingLists"/> so a snapshot push does not null the combo's selection.
    /// </summary>
    public ObservableCollection<RoutingListChoice> RoutingCatalogueOptions { get; } = [RoutingListChoice.None, RoutingListChoice.NewList];

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
        : Loc.Instance.Get("MainVm_NoAgentConnection");

    /// <summary>
    /// Whether the connect / disconnect button is actionable: the agent pipe is up AND a complete profile is
    /// chosen in the main-window combo (a configuration must be assigned - there is nothing to dial without
    /// one; routing defaults to «Полный туннель» and is always valid). Disconnect is always allowed once a
    /// tunnel is up, so an in-flight/active tunnel keeps the button live even if the row briefly reads
    /// incomplete during a reconcile.
    /// </summary>
    public bool CanToggleConnection => IsConnected && (IsTunnelActive || (ActiveProfile is { IsComplete: true }));

    // --- Power-button connection control (design "Кнопка-питание"): a round on/off circle with the
    // status and a hint beside it, tinted by state (disconnected / connecting / connected). ---
    private static readonly IBrush _circleBlue = new SolidColorBrush(Color.FromRgb(0x2A, 0x6F, 0xDB));
    private static readonly IBrush _circleBorderGray = new SolidColorBrush(Color.FromRgb(0xD9, 0xDD, 0xE6));
    private static readonly IBrush _circleBorderAmber = new SolidColorBrush(Color.FromRgb(0xF0, 0xD3, 0xA8));
    private static readonly IBrush _glyphGray = new SolidColorBrush(Color.FromRgb(0x7B, 0x81, 0x8D));
    private static readonly IBrush _glyphAmber = new SolidColorBrush(Color.FromRgb(0xE0, 0x90, 0x2F));
    private static readonly IBrush _textBlue = new SolidColorBrush(Color.FromRgb(0x1A, 0x50, 0xB0));
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

    /// <summary>Hint line under the status in the power control. When disconnected, it explains why Connect
    /// may be disabled: no profile chosen, or the chosen profile has no configuration yet.</summary>
    public string ConnectHint => ConnState switch
    {
        1 => Loc.Instance.Get("MainVm_ConnectHintConnecting"),
        2 => Loc.Instance.Get("MainVm_ConnectHintClickToDisconnect"),
        _ when ActiveProfile is null => Loc.Instance.Get("MainVm_ConnectHintSelectProfile"),
        _ when ActiveProfile is { IsComplete: false } => Loc.Instance.Get("MainVm_ConnectHintNoConfig"),
        _ => Loc.Instance.Get("MainVm_ConnectHintClickToConnect"),
    };

    /// <summary>Label on the connect/disconnect pill button in the settings header.</summary>
    public string ConnectPillContent => IsTunnelActive ? Loc.Instance.Get("MainVm_Disconnect") : Loc.Instance.Get("MainVm_Connect");

    /// <summary>Power circle fill.</summary>
    public IBrush ConnectCircleBrush => ConnState == 2 ? _circleBlue : Brushes.White;

    /// <summary>Power circle border.</summary>
    public IBrush ConnectCircleBorderBrush => ConnState switch { 2 => Brushes.Transparent, 1 => _circleBorderAmber, _ => _circleBorderGray };

    /// <summary>Power circle glyph / spinner colour.</summary>
    public IBrush ConnectCircleForeground => ConnState switch { 2 => Brushes.White, 1 => _glyphAmber, _ => _glyphGray };

    /// <summary>Status label colour in the power control.</summary>
    public IBrush ConnectStatusBrush => ConnState switch { 2 => _textBlue, 1 => _textAmber, _ => _textGray };

    /// <summary>Hint label colour in the power control.</summary>
    public IBrush ConnectHintBrush => _hintBrush;

    /// <summary>Disc colour for the tray icon: blue connected, amber connecting/transient, grey off.
    /// Single source for the tray tint so it tracks the on-screen power control's three states.</summary>
    public Color TrayStatusColor => ConnState switch
    {
        2 => Color.FromRgb(0x2A, 0x6F, 0xDB),
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

    /// <summary>Whether a profile is opened for editing in the Profile settings section.</summary>
    public bool IsProfileDetail => OpenProfile is not null;

    /// <summary>Whether the inline name editor can save (the typed name is not blank).</summary>
    public bool CanSaveProfileName => !string.IsNullOrWhiteSpace(ProfileRename);

    /// <summary>Whether the inline config-name editor has a non-blank name to save.</summary>
    public bool CanSaveConfigName => !string.IsNullOrWhiteSpace(ConfigRename);

    /// <summary>Whether the Config-section import form can save: its text parses as a config (.conf / vpn://).
    /// A name is optional (auto-derived on import), so only the config text is required.</summary>
    public bool CanSaveSectionConfig => VpnLinkCodec.TryDecode(SectionConfigText) is not null;

    /// <summary>Whether a member config is opened for management (the full config page shows on the right).</summary>
    public bool IsConfigManage => OpenConfig is not null;

    /// <summary>Whether the Profile settings section is selected (pick / edit a profile = config × routing pair).</summary>
    public bool IsSettingsProfile => SettingsSection == "profile";

    /// <summary>Whether the Config settings section is selected (the standalone config catalogue).</summary>
    public bool IsSettingsConfig => SettingsSection == "config";

    /// <summary>Whether the Routing settings section is selected (the standalone routing-list catalogue).</summary>
    public bool IsSettingsRouting => SettingsSection == "routing";

    /// <summary>Whether the General settings section is selected (About is folded into this section).</summary>
    public bool IsSettingsGeneral => SettingsSection == "general";

    /// <summary>Whether the Logs settings section is selected (the agent journal lives here now).</summary>
    public bool IsSettingsLogs => SettingsSection == "logs";

    /// <summary>Whether the Sources settings section is selected (the geo-data bases live here now).</summary>
    public bool IsSettingsSources => SettingsSection == "sources";

    /// <summary>
    /// Whether the rule editor is shown in the Routing settings section (a list is selected).
    /// </summary>
    public bool HasRoutingEditor => RoutingEditor is not null;

    /// <summary>
    /// Current theme label shown on the toggle button.
    /// </summary>
    public string ThemeLabel => IsDark ? Loc.Instance.Get("Theme_Dark") : Loc.Instance.Get("Theme_Light");

    // Maps the persisted language token to/from the combo index (0 = follow system, 1 = ru, 2 = en) (#106).
    private static int IndexForLanguage(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "ru" => 1,
        "en" => 2,
        _ => 0,
    };

    private static string TokenForLanguageIndex(int index) => index switch
    {
        1 => "ru",
        2 => "en",
        _ => Loc.SystemToken,
    };

    // A language pick persists its token and switches the UI culture live; the culture change then re-reads
    // every {l:Tr} binding and the code-computed labels below.
    partial void OnSelectedLanguageIndexChanged(int value)
    {
        var token = TokenForLanguageIndex(value);
        _prefs.Language = token;
        _prefs.Save();
        Loc.Instance.SetCulture(token);
    }

    private void OnCultureChanged()
    {
        // Only the "System" combo entry is culture-dependent; refresh it in place (the index, and so the
        // selection, is preserved). Code-computed labels re-read via their PropertyChanged.
        if (Languages.Count > 0)
        {
            Languages[0] = Loc.Instance.Get("Lang_System");
        }

        OnPropertyChanged(nameof(ThemeLabel));
    }

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
        OpenProfile = null;
    }

    [RelayCommand]
    private void NavSettings()
    {
        // Gearing into Settings lands on the active profile: open it so the Profile section shows it and its
        // configuration (OnOpenProfileChanged) + routing follow into those sections.
        if (ActiveProfile is not null)
        {
            OpenProfile = ActiveProfile;
        }

        Nav = "settings";
    }

    [RelayCommand]
    private void SelectSettings(string section)
    {
        SettingsSection = section;
    }

    // Delete the opened config from the catalogue (and the open profile's members), then return to the
    // config list. The agent refuses while the config is in use by the running profile, so on a non-OK
    // ack the view stays put and shows why.
    [RelayCommand]
    private async Task DeleteOpenConfig()
    {
        if (OpenConfig is null)
        {
            return;
        }

        ConfigDeleteStatus = string.Empty;
        var config = OpenConfig;

        // Config settings section (no open profile): just remove from the shared catalogue. The agent refuses
        // while the config backs any profile / the running tunnel, so on a non-OK ack the view stays put.
        if (OpenProfile is null)
        {
            var catalogueAck = await RemoveConfigAsync(config);
            if (catalogueAck.Ok)
            {
                OpenConfig = null;
            }
            else
            {
                ConfigDeleteStatus = catalogueAck.Message;
            }

            return;
        }

        // Configs are a shared catalogue (#45): a config can back several profiles. Only remove it from the
        // catalogue when NO OTHER profile still uses it; if it is shared, just unbind it from THIS profile
        // and leave the others' bindings intact (otherwise deleting from one profile would silently strip
        // the config from the rest).
        var sharedByOthers = Balancers.Any(b =>
            !ReferenceEquals(b, OpenProfile) && string.Equals(b.Config, config, StringComparison.Ordinal));
        if (sharedByOthers)
        {
            await SaveBalancerAsync(OpenProfile.Name, string.Empty);
            OpenConfig = null;
            return;
        }

        // Last user: delete it from the catalogue (and unbind this profile). The agent still refuses while
        // the config is in use by the running profile, so on a non-OK ack the view stays put and shows why.
        var ack = await OpenProfile.DeleteConfigAsync(config);
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

    // "✎" in the config header: switch the name to an inline editor seeded with the current name.
    [RelayCommand]
    private void BeginConfigNameEdit()
    {
        ConfigRename = OpenConfig ?? string.Empty;
        ConfigRenameStatus = string.Empty;
        IsEditingConfigName = true;
    }

    // "✕" next to the inline config-name editor: leave rename mode without saving, restoring the name.
    [RelayCommand]
    private void CancelConfigNameEdit()
    {
        ConfigRename = OpenConfig ?? string.Empty;
        ConfigRenameStatus = string.Empty;
        IsEditingConfigName = false;
    }

    // Build the config page's view model when a config is opened; null it out when the page closes.
    partial void OnOpenConfigChanged(string? value)
    {
        ConfigDeleteStatus = string.Empty;
        ConfigRename = value ?? string.Empty;
        ConfigRenameStatus = string.Empty;
        IsEditingConfigName = false;
        SyncCatalogueConfig();
        if (value is null)
        {
            ConfigExport = null;
            ConfigTransport = null;
            return;
        }

        var export = new ExportDialogViewModel(_connection, value);
        ConfigExport = export;
        _ = export.LoadAsync();

        // Seed the WebSocket (UDP-over-TCP) transport editor from the opened config's current snapshot
        // values. The per-config DNS / exclusions editors are retired: DNS + exclusions are now per-routing
        // (see RoutingSettingsViewModel), so they are no longer constructed or bound here.
        var item = Configs.FirstOrDefault(c => string.Equals(c.Name, value, StringComparison.Ordinal));
        ConfigTransport = new ConfigTransportViewModel(_connection, value, item?.Endpoint ?? string.Empty, item?.UseWebSocket ?? false, item?.WebSocketHost ?? string.Empty, item?.WebSocketPort ?? 443, item?.Mtu ?? 0);
    }

    // The Config section combo pick: «— не выбрано —» closes the open config / create form; «+ Новая
    // конфигурация» opens the import form (also how a profile's "+ Новая конфигурация" redirect lands here);
    // a real config opens it for editing.
    partial void OnSelectedCatalogueConfigChanged(ConfigChoice? value)
    {
        if (_suppressCatalogueConfig || value is null)
        {
            return;
        }

        if (value.IsNewSentinel)
        {
            OpenConfig = null; // hide the editor; the create form is the only thing shown
            BeginSectionConfig();
            return;
        }

        // «— не выбрано —» or a real config: leave the create form and open (or clear) the selection.
        if (IsCreatingSectionConfig)
        {
            CancelSectionConfig();
        }

        OpenConfig = value.IsReal ? value.Name : null;
    }

    // Opening / closing the create form re-mirrors the combo (BeginSectionConfig -> «+ Новая конфигурация»,
    // Cancel/Save -> «— не выбрано —» or the config just opened).
    partial void OnIsCreatingSectionConfigChanged(bool value)
    {
        SyncCatalogueConfig();
    }

    // Mirror the Config section's state (create form open / a config open / neither) into its combo without
    // echoing the pick back.
    private void SyncCatalogueConfig()
    {
        _suppressCatalogueConfig = true;
        SelectedCatalogueConfig = IsCreatingSectionConfig
            ? ConfigChoice.NewConfig
            : OpenConfig is null
                ? ConfigChoice.None
                : ConfigCatalogueOptions.FirstOrDefault(o => o.IsReal && string.Equals(o.Name, OpenConfig, StringComparison.Ordinal)) ?? ConfigChoice.None;
        _suppressCatalogueConfig = false;
    }

    // Reconcile ConfigCatalogueOptions in place from the config names: keep «— не выбрано —» at [0] and
    // «+ Новая конфигурация» at [1], reconcile the real (name) choices after them - the same in-place scheme
    // as ReconcileProfileOptions, so a snapshot push does not reset the combo's selection.
    private void ReconcileConfigCatalogueOptions()
    {
        const int head = 2; // None + NewConfig occupy [0] and [1].
        var present = _configNames.ToHashSet(StringComparer.Ordinal);
        for (var i = ConfigCatalogueOptions.Count - 1; i >= head; i--)
        {
            if (!present.Contains(ConfigCatalogueOptions[i].Name))
            {
                ConfigCatalogueOptions.RemoveAt(i);
            }
        }

        for (var i = 0; i < _configNames.Count; i++)
        {
            var name = _configNames[i];
            var slot = head + i;
            var existing = ConfigCatalogueOptions.Skip(head).FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));
            if (existing is null)
            {
                ConfigCatalogueOptions.Insert(Math.Min(slot, ConfigCatalogueOptions.Count), new ConfigChoice(name));
                continue;
            }

            var index = ConfigCatalogueOptions.IndexOf(existing);
            if (index != slot)
            {
                ConfigCatalogueOptions.Move(index, slot);
            }
        }
    }

    // The main-window profile combo changed. A real user pick selects that profile on the agent and persists
    // its name; a programmatic assignment during a snapshot reconcile (guarded by _suppressActivePush) only
    // updates the combo so it mirrors the agent's selected target without echoing OpSelectProfile back.
    partial void OnActiveProfileChanged(BalancerItemViewModel? oldValue, BalancerItemViewModel? newValue)
    {
        // Follow the active profile's completeness (a config must be assigned before it can be dialed), so
        // the power button re-gates the moment its config is set or cleared under it.
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnActiveProfilePropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnActiveProfilePropertyChanged;
        }

        // Always mirror the wrapped selection so the combo shows «— не выбрано —» / the profile name,
        // whether the change came from a user pick or a snapshot reconcile.
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

    // Re-gate the power button when the active profile's assigned config changes (completeness depends on it).
    private void OnActiveProfilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BalancerItemViewModel.Config) or nameof(BalancerItemViewModel.IsComplete))
        {
            NotifyCanToggleConnection();
        }
    }

    private void NotifyCanToggleConnection()
    {
        OnPropertyChanged(nameof(CanToggleConnection));
        OnPropertyChanged(nameof(ConnectHint));
        ToggleConnectionCommand.NotifyCanExecuteChanged();
    }

    // The main-window profile combo's wrapped selection changed. Translate the pick: a real profile becomes
    // the active target, «— не выбрано —» clears it, and «+ Новый профиль» redirects into the Profile section
    // to create one there (the combo itself is reverted first so it does not stick on the sentinel).
    partial void OnActiveProfileChoiceChanged(ProfileChoice? value)
    {
        if (_suppressActiveChoice || value is null)
        {
            return;
        }

        if (value.IsNew)
        {
            SyncActiveProfileChoice();
            Nav = "settings";
            SettingsSection = "profile";
            _ = CreateProfile();
            return;
        }

        ActiveProfile = value.IsReal
            ? Balancers.FirstOrDefault(b => string.Equals(b.Name, value.Name, StringComparison.Ordinal))
            : null;
    }

    // The Profile-section combo's wrapped selection changed: a real profile opens in the editor, «— не
    // выбрано —» closes it, «+ Новый профиль» creates one (we are already in the section) and opens it.
    partial void OnOpenProfileChoiceChanged(ProfileChoice? value)
    {
        if (_suppressOpenChoice || value is null)
        {
            return;
        }

        if (value.IsNew)
        {
            SyncOpenProfileChoice();
            _ = CreateProfile();
            return;
        }

        OpenProfile = value.IsReal
            ? Balancers.FirstOrDefault(b => string.Equals(b.Name, value.Name, StringComparison.Ordinal))
            : null;
    }

    // Mirror ActiveProfile into the main-window combo's wrapped selection without echoing back a pick.
    private void SyncActiveProfileChoice()
    {
        _suppressActiveChoice = true;
        ActiveProfileChoice = ActiveProfile is null
            ? ProfileChoice.None
            : ProfileOptions.FirstOrDefault(o => o.IsReal && string.Equals(o.Name, ActiveProfile.Name, StringComparison.Ordinal)) ?? ProfileChoice.None;
        _suppressActiveChoice = false;
    }

    // Mirror OpenProfile into the Profile-section combo's wrapped selection without echoing back a pick.
    private void SyncOpenProfileChoice()
    {
        _suppressOpenChoice = true;
        OpenProfileChoice = OpenProfile is null
            ? ProfileChoice.None
            : ProfileOptions.FirstOrDefault(o => o.IsReal && string.Equals(o.Name, OpenProfile.Name, StringComparison.Ordinal)) ?? ProfileChoice.None;
        _suppressOpenChoice = false;
    }

    // The Routing section's edit combo changed: build the rule editor and per-routing settings for the
    // selected list. A null pick is ignored (combo-rebuild artifact); the create-new path is a command.
    partial void OnEditRoutingListChanged(RoutingListSummaryViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        BuildSectionRoutingEditor(value.Id, value.Name);
        // Selecting the just-saved list (or re-selecting the open one) short-circuits BuildSectionRoutingEditor,
        // so RoutingEditor does not change and OnRoutingEditorChanged will not fire - mirror the combo here.
        SyncCatalogueRouting();
    }

    // The Routing section combo pick: «— не выбрано —» closes the editor; «+ Новый список» opens a new-list
    // draft (also how a profile's "+ Новый список" redirect lands here); a real list opens it for editing.
    partial void OnSelectedCatalogueRoutingChanged(RoutingListChoice? value)
    {
        if (_suppressCatalogueRouting || value is null)
        {
            return;
        }

        if (value.IsNewSentinel)
        {
            CreateRoutingList();
            return;
        }

        if (value.IsNone)
        {
            EditRoutingList = null;
            RoutingEditor = null;
            RoutingSettings = null;
            return;
        }

        var row = RoutingLists.FirstOrDefault(r => r.Id == value.Id);
        if (row is not null)
        {
            EditRoutingList = row;
        }
    }

    // The section rule-editor instance changed (new draft created, real list opened, or closed): re-mirror
    // the combo. An IsNew draft shows «+ Новый список»; a real editor is reflected via EditRoutingList.
    partial void OnRoutingEditorChanged(RoutingListEditorViewModel? oldValue, RoutingListEditorViewModel? newValue)
    {
        SyncCatalogueRouting();
    }

    // Mirror the Routing section's state into its combo without echoing the pick back: a new-list draft shows
    // «+ Новый список», a selected real list shows its row, otherwise «— не выбрано —».
    private void SyncCatalogueRouting()
    {
        _suppressCatalogueRouting = true;
        SelectedCatalogueRouting = RoutingEditor is { IsNew: true }
            ? RoutingListChoice.NewList
            : EditRoutingList is null
                ? RoutingListChoice.None
                : RoutingCatalogueOptions.FirstOrDefault(o => o.IsReal && o.Id == EditRoutingList.Id) ?? RoutingListChoice.None;
        _suppressCatalogueRouting = false;
    }

    // Reconcile RoutingCatalogueOptions in place from RoutingLists: keep «— не выбрано —» at [0] and «+ Новый
    // список» at [1], reconcile the real (id) choices after them - replacing a renamed row so its label
    // updates. A following SyncCatalogueRouting re-selects by id, so a replace never strands the selection.
    private void ReconcileRoutingCatalogueOptions()
    {
        const int head = 2; // None + NewList occupy [0] and [1].
        var present = RoutingLists.Select(r => r.Id).ToHashSet();
        for (var i = RoutingCatalogueOptions.Count - 1; i >= head; i--)
        {
            if (RoutingCatalogueOptions[i].Id is not long id || !present.Contains(id))
            {
                RoutingCatalogueOptions.RemoveAt(i);
            }
        }

        for (var i = 0; i < RoutingLists.Count; i++)
        {
            var row = RoutingLists[i];
            var slot = head + i;
            var existing = RoutingCatalogueOptions.Skip(head).FirstOrDefault(o => o.Id == row.Id);
            if (existing is null)
            {
                RoutingCatalogueOptions.Insert(Math.Min(slot, RoutingCatalogueOptions.Count), new RoutingListChoice(row.Id, row.Name));
                continue;
            }

            if (!string.Equals(existing.Name, row.Name, StringComparison.Ordinal))
            {
                RoutingCatalogueOptions[RoutingCatalogueOptions.IndexOf(existing)] = new RoutingListChoice(row.Id, row.Name);
                existing = RoutingCatalogueOptions.Skip(head).First(o => o.Id == row.Id);
            }

            var index = RoutingCatalogueOptions.IndexOf(existing);
            if (index != slot)
            {
                RoutingCatalogueOptions.Move(index, slot);
            }
        }
    }

    // Builds the Routing section's rule editor + per-routing settings for a real (saved) list. Independent of
    // any open profile - the section catalogue is standalone.
    private void BuildSectionRoutingEditor(long id, string name)
    {
        if (RoutingEditor is not null && RoutingEditor.Id == id && !RoutingEditor.IsNew)
        {
            return;
        }

        var editor = new RoutingListEditorViewModel(_connection, id, name, OnSectionRoutingEditorSaved);
        RoutingEditor = editor;
        _ = editor.LoadAsync();

        var settings = new RoutingSettingsViewModel(_connection, id);
        RoutingSettings = settings;
        _ = settings.LoadAsync();
    }

    // When the Routing section's new (id=0) list is first saved it gets a real id: build its per-routing
    // settings and pin the selection to the freshly-created list. The list's summary row is not in
    // RoutingLists yet (it arrives on the next snapshot), so remember the id and let SyncRoutingLists select
    // it once present; if it is already there, select it now. The editor has already cleared its IsNew flag,
    // so re-selecting it will not rebuild it (BuildSectionRoutingEditor short-circuits on the same real id).
    private void OnSectionRoutingEditorSaved(long id)
    {
        var settings = new RoutingSettingsViewModel(_connection, id);
        RoutingSettings = settings;
        _ = settings.LoadAsync();

        var created = RoutingLists.FirstOrDefault(r => r.Id == id);
        if (created is not null)
        {
            _pendingEditRoutingListId = null;
            EditRoutingList = created;
        }
        else
        {
            _pendingEditRoutingListId = id;
        }
    }

    // Track the open profile so its SelectedRoutingList drives the inline rule editor. Subscribing to the
    // instance is safe because the snapshot reconcile keeps the same BalancerItemViewModel instances.
    partial void OnOpenProfileChanged(BalancerItemViewModel? oldValue, BalancerItemViewModel? newValue)
    {
        // Open the profile's single configuration so its editors (text / proxy) are available on the rail.
        OpenConfig = string.IsNullOrEmpty(newValue?.Config) ? null : newValue!.Config;

        // Reset the rename field only when the profile identity actually changes. A background snapshot can
        // re-assign the open profile to a refreshed instance of the SAME profile; without this guard that
        // would wipe the half-typed name mid-edit.
        var sameProfile = oldValue is not null && newValue is not null
            && string.Equals(oldValue.Name, newValue.Name, StringComparison.Ordinal);
        if (!sameProfile)
        {
            ProfileRename = newValue?.Name ?? string.Empty;
            ProfileRenameStatus = string.Empty;
            // Reflect the profile's routing list into the Routing section too (mirrors OpenConfig above), so
            // opening a profile - or gearing into Settings on the active one - lands the Routing section on the
            // list this profile actually uses. Guarded to real identity changes so a background snapshot re-bind
            // of the SAME profile does not yank a routing list the user is editing.
            OpenProfileRouting(newValue);
        }

        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnOpenProfilePropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnOpenProfilePropertyChanged;
        }

        // Mirror the open profile into the Profile-section combo so it shows «— не выбрано —» / the name.
        SyncOpenProfileChoice();

        // The rule editor / per-routing settings belong to the standalone Routing section now, not to a
        // profile, so opening/closing a profile no longer touches RoutingEditor.
    }

    private void OnOpenProfilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BalancerItemViewModel.Config))
        {
            OpenConfig = string.IsNullOrEmpty(OpenProfile?.Config) ? null : OpenProfile!.Config;
        }
    }

    // Reflect a profile's assigned routing list into the standalone Routing section: a real list opens there
    // (its rule / per-routing settings editors build via OnEditRoutingListChanged), while no list clears the
    // section. Mirrors how OnOpenProfileChanged opens the profile's configuration.
    private void OpenProfileRouting(BalancerItemViewModel? profile)
    {
        var choice = profile?.SelectedRoutingList;
        if (choice is { IsReal: true } && RoutingLists.FirstOrDefault(r => r.Id == choice.Id) is { } row)
        {
            EditRoutingList = row;
            return;
        }

        EditRoutingList = null;
        RoutingEditor = null;
        RoutingSettings = null;
        SyncCatalogueRouting();
    }

    // When the routing editor is swapped out (list switch, close, profile change, disconnect), detach its
    // auto-save: a persisted list with a queued edit is flushed so navigating away does not lose it, while an
    // un-persisted "+ Новый список" draft is abandoned (so it leaves no orphan).
    partial void OnRoutingEditorChanging(RoutingListEditorViewModel? oldValue, RoutingListEditorViewModel? newValue)
    {
        oldValue?.DetachAutoSave();
    }

    // The rule editor auto-saves (DetachAutoSave above flushes it), but the per-routing settings (DNS /
    // exclusions / all-UDP) save only on «Сохранить». Flush a dirty settings editor before swapping it out
    // (list switch / create / delete / disconnect) so navigating away does not silently drop those edits.
    partial void OnRoutingSettingsChanging(RoutingSettingsViewModel? oldValue, RoutingSettingsViewModel? newValue)
    {
        if (oldValue is { Dirty: true })
        {
            _ = oldValue.SaveAsync();
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

        _prefs.IsDark = IsDark;
        _prefs.Save();
    }

    // Persist the selected settings section (#51) whenever it changes.
    partial void OnSettingsSectionChanged(string value)
    {
        _prefs.SettingsSection = value;
        _prefs.Save();
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
            // Tear down all the section editing state too: their backing rows are about to be cleared, so a
            // stale editor would keep firing IPC at a dead pipe until the next reconnect snapshot rebuilds the
            // lists. The combos null out with their cleared sources; clearing the targets here is explicit.
            OpenProfile = null;
            ActiveProfile = null;
            // Drop the stale profile names from the combos (Balancers is now empty), leaving «— не выбрано —»
            // and «+ Новый профиль»; the selections already re-mirrored to None via the nulls above.
            ReconcileProfileOptions();
            OpenConfig = null;
            EditRoutingList = null;
            RoutingSettings = null;
            BoundTarget = null;
            _pendingOpenConfig = null;
            _pendingEditRoutingListId = null;
            _configNames = [];
            // Drop the stale catalogue rows from the section combos and re-mirror them to «— не выбрано —».
            IsCreatingSectionConfig = false;
            ReconcileConfigCatalogueOptions();
            ReconcileRoutingCatalogueOptions();
            SyncCatalogueConfig();
            SyncCatalogueRouting();
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

        // Mirror the agent's selected target into the main-window profile combo without echoing a select
        // back. Prefer the agent's active/selected target; fall back to the last profile the user had
        // chosen (restored from prefs) so the window opens on it with connect still gated until present.
        _suppressActivePush = true;
        var active = Balancers.FirstOrDefault(b => ProfileMatchesTarget(b, selected));
        if (active is null && !string.IsNullOrEmpty(_prefs.LastProfile))
        {
            active = Balancers.FirstOrDefault(b => string.Equals(b.Name, _prefs.LastProfile, StringComparison.Ordinal));
        }
        if (active is not null)
        {
            ActiveProfile = active;
        }
        else if (ActiveProfile is not null && !Balancers.Contains(ActiveProfile))
        {
            // The chosen profile was removed elsewhere: drop the selection so connect re-gates.
            ActiveProfile = null;
        }
        _suppressActivePush = false;

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
                ? Loc.Instance.Get("MainVm_NoticeProfileEmpty", snapshot.SelectedTarget)
                : Loc.Instance.Get("MainVm_NoticeConnectFailed");
        }
        else if (snapshot.Active && SelectedDiffersFromBound(snapshot))
        {
            notice = Loc.Instance.Get("MainVm_NoticeProfileSelected", snapshot.SelectedTarget);
        }
        else if (snapshot.RestartRequired)
        {
            notice = Loc.Instance.Get("MainVm_NoticeSettingsChanged");
        }

        ShowNotice(notice);

        _suppressSettingPush = true;
        GeoAutoCheck = snapshot.GeoAutoCheck;
        EnsureGeoInterval(snapshot.GeoCheckIntervalHours);
        GeoCheckIntervalHours = snapshot.GeoCheckIntervalHours;
        EnsureGeoValidity(snapshot.GeoCacheValidityHours);
        GeoCacheValidityHours = snapshot.GeoCacheValidityHours;
        LogLevelLabel = LabelForLogToken(snapshot.LogLevel);
        RouteLogEnabled = snapshot.RouteLog;
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

    partial void OnGeoCacheValidityHoursChanged(int value)
    {
        if (!_suppressSettingPush && value > 0)
        {
            _ = _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting,
                ["geo-cache-validity-hours", value.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
        }
    }

    partial void OnLogLevelLabelChanged(string value)
    {
        if (!_suppressSettingPush)
        {
            _ = _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting,
                ["log-level", TokenForLogLabel(value)]));
        }
    }

    partial void OnRouteLogEnabledChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync("route-log", value);
        }
    }

    // Maps the persisted verbosity token to its display label and back. An unknown token shows as "Обычный"
    // so the combo never goes null (which, two-way bound, would push an empty value back).
    private static string LabelForLogToken(string token)
    {
        return token switch
        {
            "trace" => Loc.Instance.Get("MainVm_LogLevelTrace"),
            "debug" => Loc.Instance.Get("MainVm_LogLevelDebug"),
            _ => Loc.Instance.Get("MainVm_LogLevelNormal"),
        };
    }

    private static string TokenForLogLabel(string label)
    {
        if (string.Equals(label, Loc.Instance.Get("MainVm_LogLevelTrace"), StringComparison.Ordinal))
        {
            return "trace";
        }

        if (string.Equals(label, Loc.Instance.Get("MainVm_LogLevelDebug"), StringComparison.Ordinal))
        {
            return "debug";
        }

        return "info";
    }

    // Keeps the validity combo able to display whatever the agent reports (an out-of-band value set via CLI),
    // mirroring EnsureGeoInterval, so the ComboBox SelectedItem never goes null and writes 0 back.
    private void EnsureGeoValidity(int hours)
    {
        if (hours <= 0 || GeoCacheValidities.Contains(hours))
        {
            return;
        }

        var index = 0;
        while (index < GeoCacheValidities.Count && GeoCacheValidities[index] < hours)
        {
            index++;
        }

        GeoCacheValidities.Insert(index, hours);
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

    /// <summary>
    /// Asks the agent to build a redacted diagnostics bundle (#82) and returns the agent-side zip path (under
    /// ProgramData, readable by this UI) so the window can copy it to a user-chosen file. Returns null on
    /// failure, after showing a notice. The build runs agent-side because only SYSTEM can read both
    /// processes' logs.
    /// </summary>
    public async Task<string?> RequestDiagnosticsAsync()
    {
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpCollectDiagnostics, []));
        if (!ack.Ok)
        {
            ShowNotice(string.IsNullOrWhiteSpace(ack.Message) ? Loc.Instance.Get("MainVm_DiagnosticsFailed") : ack.Message);
            return null;
        }

        return ack.Message;
    }

    /// <summary>
    /// Shows a transient notice on behalf of the window code-behind (e.g. after saving the diagnostics bundle).
    /// </summary>
    public void ShowTransientNotice(string message)
    {
        ShowNotice(message);
    }

    /// <summary>
    /// Whether an update URL is configured (baked into the build from installer.config.json). When false the
    /// update section and its check control are hidden - there is nothing to check against.
    /// </summary>
    public bool HasUpdateUrl => !string.IsNullOrWhiteSpace(UpdateUrl);

    // Applies the update-related snapshot fields. The URL is baked into the build and surfaced read-only
    // (it drives only HasUpdateUrl, which gates the update UI); a freshly available version raises the banner.
    private void ApplyUpdateState(StatusSnapshot snapshot)
    {
        AppVersion = $"AmneziaGeo {(string.IsNullOrEmpty(snapshot.AgentVersion) ? "-" : snapshot.AgentVersion)}";
        AmneziaVersion = string.IsNullOrEmpty(snapshot.EngineVersion) ? Loc.Instance.Get("MainVm_NotAvailable") : snapshot.EngineVersion;

        UpdateUrl = snapshot.UpdateUrl;

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
        UpdateStatus = Loc.Instance.Get("MainVm_UpdateChecking");
        // The URL is baked into the build (installer config), not user-entered; just ask for a check.
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
        UpdateStatus = Loc.Instance.Get("MainVm_UpdateDownloading");
        try
        {
            var path = await DownloadSetupAsync(_updateSetupUrl, new Progress<int>(p => UpdateDownloadPercent = p));
            UpdateStatus = Loc.Instance.Get("MainVm_UpdateLaunching");

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
            UpdateStatus = Loc.Instance.Get("MainVm_UpdateError", ex.Message);
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
            existing.Mtu = entry.Mtu;
        }

        _configNames = [.. entries.Select(e => e.Name)];
        ReconcileConfigCatalogueOptions();

        // A config just imported in the Config section: open it once its row arrives so OnOpenConfigChanged
        // seeds the transport editor from the real snapshot row instead of all-defaults.
        if (_pendingOpenConfig is not null && present.Contains(_pendingOpenConfig))
        {
            var name = _pendingOpenConfig;
            _pendingOpenConfig = null;
            OpenConfig = name;
        }

        // Re-select the section combo now that the option list is current: an OpenConfig set above (or a
        // pending one just resolved) whose real choice only now exists needs the selection re-pointed at it.
        SyncCatalogueConfig();
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

        ReconcileRoutingCatalogueOptions();

        // Reconcile the Routing section's selected list: if it was removed elsewhere, drop the section editor;
        // if its instance was replaced by a fresh row of the same id, re-point at the surviving instance so
        // the combo stays selected. The reconcile above keeps instances in place, so this only fires on a
        // genuine removal. A pending new-list draft (RoutingEditor.IsNew) is left alone.
        if (EditRoutingList is not null && !RoutingLists.Contains(EditRoutingList))
        {
            var same = RoutingLists.FirstOrDefault(r => r.Id == EditRoutingList.Id);
            if (same is not null)
            {
                EditRoutingList = same;
            }
            else if (RoutingEditor is not { IsNew: true })
            {
                EditRoutingList = null;
                RoutingEditor = null;
                RoutingSettings = null;
            }
        }

        // A list just created in the Routing section: once its summary row arrives, select it so the combo
        // shows it and «Удалить» becomes available. The editor already cleared IsNew on its first save, so
        // selecting it short-circuits BuildSectionRoutingEditor (no rebuild, no re-fetch, no lost edits).
        if (_pendingEditRoutingListId is long pendingId)
        {
            var row = RoutingLists.FirstOrDefault(r => r.Id == pendingId);
            if (row is not null)
            {
                _pendingEditRoutingListId = null;
                EditRoutingList = row;
            }
        }

        // Re-select the section combo now that the option list is current (a just-created list's real choice
        // only now exists; a renamed row was replaced above).
        SyncCatalogueRouting();
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
            existing.Error = entry.Error;
        }

        HasSources = Sources.Count > 0;

        // Refresh the open routing editor's category suggestions when the set of available categories
        // actually changed (a source finished downloading, or one was added / removed). Gated on a
        // signature so an unrelated snapshot tick (progress %, update badge) does not re-fetch list-geo.
        var signature = string.Join('|', entries
            .Select(e => $"{e.Name}={e.CategoryCount}")
            .OrderBy(s => s, StringComparer.Ordinal));
        if (signature != _geoCategorySignature)
        {
            _geoCategorySignature = signature;
            _ = RoutingEditor?.RefreshSuggestionsAsync();
        }
    }

    private void SyncBalancers(IReadOnlyList<BalancerEntry> entries, IReadOnlyList<RoutingListEntry> routingLists)
    {
        var options = BuildRoutingOptions(routingLists);
        var configOptions = BuildConfigOptions();

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
                existing = new BalancerItemViewModel(SaveBalancerAsync, AssignRoutingAsync, SelectProfileAsync, ToggleProfileConnectionAsync, RemoveConfigAsync, RequestNewConfigForProfile, RequestNewRoutingList);
                existing.ApplyFromEntry(entry, options, configOptions);
                Balancers.Insert(Math.Min(i, Balancers.Count), existing);
                continue;
            }

            existing.ApplyFromEntry(entry, options, configOptions);
            var index = Balancers.IndexOf(existing);
            if (index != i)
            {
                Balancers.Move(index, i);
            }
        }

        // Keep the shared combo options in step with the profile rows (reconciled in place so neither combo's
        // selection is nulled), then re-mirror both selections onto the refreshed option instances.
        ReconcileProfileOptions();

        // If the profile opened in the detail view was removed elsewhere, fall back to the list.
        if (OpenProfile is not null && !Balancers.Contains(OpenProfile))
        {
            OpenProfile = null;
        }

        // Open a profile just created via "+ Профиль" straight into its editor so a config/routing can be
        // picked immediately.
        if (_pendingOpenProfile is not null)
        {
            var created = Balancers.FirstOrDefault(b => string.Equals(b.Name, _pendingOpenProfile, StringComparison.Ordinal));
            if (created is not null)
            {
                OpenProfile = created;
                _pendingOpenProfile = null;
            }
        }

        SyncActiveProfileChoice();
        SyncOpenProfileChoice();
    }

    // Reconcile ProfileOptions in place from Balancers: keep «— не выбрано —» at [0] and «+ Новый профиль»
    // at [1], and reconcile the real (name) choices after them by name - dropping removed profiles, adding
    // new ones, and reordering to match. Editing in place (rather than Clear + rebuild) keeps the None/New/
    // existing choice instances alive so a bound ComboBox's selection is not reset by the refresh.
    private void ReconcileProfileOptions()
    {
        const int head = 2; // None + New occupy [0] and [1].
        var present = Balancers.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = ProfileOptions.Count - 1; i >= head; i--)
        {
            if (!present.Contains(ProfileOptions[i].Name))
            {
                ProfileOptions.RemoveAt(i);
            }
        }

        for (var i = 0; i < Balancers.Count; i++)
        {
            var name = Balancers[i].Name;
            var slot = head + i;
            var existing = ProfileOptions.Skip(head).FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));
            if (existing is null)
            {
                ProfileOptions.Insert(Math.Min(slot, ProfileOptions.Count), new ProfileChoice(name));
                continue;
            }

            var index = ProfileOptions.IndexOf(existing);
            if (index != slot)
            {
                ProfileOptions.Move(index, slot);
            }
        }
    }

    private static IReadOnlyList<RoutingListChoice> BuildRoutingOptions(IReadOnlyList<RoutingListEntry> entries)
    {
        // Order: «Полный туннель» (None), «+ Новый список» (redirect to the Routing section), then the saved
        // lists - the same «не выбрано / добавить / сохранённые» shape as the config and profile combos.
        var options = new List<RoutingListChoice> { RoutingListChoice.None, RoutingListChoice.NewList };
        foreach (var entry in entries)
        {
            options.Add(new RoutingListChoice(entry.Id, entry.Name));
        }

        return options;
    }

    // The config catalogue for a profile's combo. Order: «— не выбрано —» (None), «+ Новая конфигурация»
    // (redirect to the Config section), then every config (shared / reusable across profiles) - the same
    // «не выбрано / добавить / сохранённые» shape as the routing and profile combos.
    private IReadOnlyList<ConfigChoice> BuildConfigOptions()
    {
        var options = new List<ConfigChoice> { ConfigChoice.None, ConfigChoice.NewConfig };
        foreach (var name in _configNames)
        {
            options.Add(new ConfigChoice(name));
        }

        return options;
    }

    // Redirect targets for a profile's config / routing combos when the user picks the "+ Новая
    // конфигурация" / "+ Новый список" sentinel: jump to the matching settings section with its create UI
    // opened, rather than an inline form inside the profile. The open profile stays put, so returning to the
    // Profile section re-shows it with the freshly-created entry now selectable in the combo.
    private void RequestNewConfigForProfile()
    {
        SettingsSection = "config";
        BeginSectionConfig();
    }

    private void RequestNewRoutingList()
    {
        SettingsSection = "routing";
        CreateRoutingList();
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
        var baseName = Loc.Instance.Get("MainVm_NewProfileDefaultName");
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

    // --- Config settings section: standalone "+ Новая конфигурация" import (adds to the shared catalogue
    // without a profile). The clipboard/QR/camera/file pickers are window concerns; here a name + pasted
    // text + «Сохранить» is the minimum, and the code-behind file picker fills SectionConfigText. ---

    /// <summary>Public so the section import form's code-behind file picker can also import to the catalogue.</summary>
    public Task<IpcAck> ImportSectionConfigAsync(string name, string confText) => ImportConfigAsync(name, confText);

    [RelayCommand]
    private void BeginSectionConfig()
    {
        // Close any open config first so only the create form shows (the redirect from a profile's picker can
        // arrive with a config still open), and so the combo lands on «+ Новая конфигурация», not a real row.
        OpenConfig = null;
        SectionConfigName = string.Empty;
        SectionConfigText = string.Empty;
        SectionConfigStatus = string.Empty;
        IsCreatingSectionConfig = true;
    }

    [RelayCommand]
    private void CancelSectionConfig()
    {
        IsCreatingSectionConfig = false;
        SectionConfigName = string.Empty;
        SectionConfigText = string.Empty;
        SectionConfigStatus = string.Empty;
    }

    [RelayCommand]
    private async Task SaveSectionConfig()
    {
        var imported = VpnLinkCodec.TryDecode(SectionConfigText);
        if (imported is null)
        {
            SectionConfigStatus = Loc.Instance.Get("MainVm_ConfigNotRecognized");
            return;
        }

        var name = !string.IsNullOrWhiteSpace(SectionConfigName)
            ? SectionConfigName.Trim()
            : (string.IsNullOrWhiteSpace(imported.Name) ? "config" : imported.Name!.Trim());

        var ack = await ImportConfigAsync(name, imported.ConfText);
        if (!ack.Ok)
        {
            SectionConfigStatus = ack.Message;
            return;
        }

        IsCreatingSectionConfig = false;
        SectionConfigName = string.Empty;
        SectionConfigText = string.Empty;
        SectionConfigStatus = string.Empty;
        // Open the just-imported config once its row lands in the next snapshot, so the transport editor seeds
        // from the real config row rather than all-defaults (the row is not in Configs yet at this point).
        _pendingOpenConfig = name;
    }

    // --- Routing settings section: standalone list CRUD (independent of any profile). ---

    // "+ Новый список": show a fresh create-editor; per-routing settings are built once it is first saved.
    [RelayCommand]
    private void CreateRoutingList()
    {
        var editor = new RoutingListEditorViewModel(_connection, OnSectionRoutingEditorSaved);
        RoutingEditor = editor;
        RoutingSettings = null;
        EditRoutingList = null;
        _ = editor.LoadAsync();
    }

    // "Удалить список" in the Routing section: delete the shared list, then clear the section editor.
    [RelayCommand]
    private async Task DeleteSectionRoutingList()
    {
        if (RoutingEditor is null)
        {
            return;
        }

        if (await RoutingEditor.DeleteAsync())
        {
            RoutingEditor = null;
            RoutingSettings = null;
            EditRoutingList = null;
        }
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

    // Rename the open profile from the Профиль aspect. On success the detail closes; the rebroadcast list
    // shows the new name. On a refused rename (name taken, or it is the running profile) the view stays put
    // and shows why.
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
