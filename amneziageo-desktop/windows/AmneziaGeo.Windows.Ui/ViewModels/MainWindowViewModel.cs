using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
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
    private string? _geoBannerSignature;
    private bool _suppressActivePush;
    private long? _pendingEditRoutingListId;
    private string? _pendingOpenConfig;
    private string _sectionConfigDefaultName = string.Empty;
    private bool _sectionConfigSaving;
    private bool _suppressActiveChoice;
    private bool _suppressOpenChoice;
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
    [NotifyCanExecuteChangedFor(nameof(CreateProfileCommand))]
    [NotifyPropertyChangedFor(nameof(ShowNoProfilesYetHint))]
    private bool _hasConfigs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelectConfigHint))]
    [NotifyPropertyChangedFor(nameof(ShowNoProfilesYetHint))]
    private bool _hasBalancers;

    [ObservableProperty]
    private bool _hasRoutingLists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome))]
    [NotifyPropertyChangedFor(nameof(IsSettings))]
    private string _nav = "home";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProfileDetail))]
    private BalancerItemViewModel? _openProfile;

    [ObservableProperty]
    private ProfileChoice? _openProfileChoice = ProfileChoice.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    private BalancerItemViewModel? _activeProfile;

    [ObservableProperty]
    private ProfileChoice? _activeProfileChoice = ProfileChoice.None;

    [ObservableProperty]
    private RoutingListSummaryViewModel? _editRoutingList;

    [ObservableProperty]
    private ConfigChoice? _selectedCatalogueConfig = ConfigChoice.None;

    [ObservableProperty]
    private RoutingListChoice? _selectedCatalogueRouting = RoutingListChoice.None;

    [ObservableProperty]
    private RoutingSettingsViewModel? _routingSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfigManage))]
    private string? _openConfig;

    [ObservableProperty]
    private ExportDialogViewModel? _configExport;

    [ObservableProperty]
    private ConfigTransportViewModel? _configTransport;

    [ObservableProperty]
    private string _configDeleteStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigNameMissing))]
    private string _configRename = string.Empty;

    [ObservableProperty]
    private string _configRenameStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileNameMissing))]
    private string _profileRename = string.Empty;

    [ObservableProperty]
    private string _profileRenameStatus = string.Empty;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoutingEditor))]
    private RoutingListEditorViewModel? _routingEditor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateUrl))]
    private string _updateUrl = string.Empty;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateVersionBadgeText))]
    [NotifyPropertyChangedFor(nameof(UpdateBannerText))]
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
    [NotifyPropertyChangedFor(nameof(GeoUpdateBannerText))]
    private int _geoUpdateCount;

    [ObservableProperty]
    private bool _geoAutoCheck = true;

    [ObservableProperty]
    private bool _isCreatingSectionConfig;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveSectionConfig))]
    [NotifyPropertyChangedFor(nameof(SectionConfigNameMissing))]
    private string _sectionConfigName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveSectionConfig))]
    private string _sectionConfigText = string.Empty;

    [ObservableProperty]
    private string _sectionConfigStatus = string.Empty;


    [ObservableProperty]
    private int _geoCheckIntervalHours = 24;

    /// <summary>
    /// Preset interval options (hours) for the geo auto-check combo.
    /// </summary>
    public ObservableCollection<int> GeoCheckIntervals { get; } = [6, 12, 24, 48, 168];

    [ObservableProperty]
    private int _geoCacheValidityHours = 24;

    /// <summary>
    /// Preset cache validity options (hours).
    /// </summary>
    public ObservableCollection<int> GeoCacheValidities { get; } = [6, 12, 24, 48, 72, 168];

    /// <summary>
    /// Log verbosity options.
    /// </summary>
    public ObservableCollection<string> LogLevels { get; } = [Loc.Instance.Get("MainVm_LogLevelNormal"), Loc.Instance.Get("MainVm_LogLevelDebug"), Loc.Instance.Get("MainVm_LogLevelTrace")];

    [ObservableProperty]
    private string _logLevelLabel = Loc.Instance.Get("MainVm_LogLevelNormal");

    [ObservableProperty]
    private bool _routeLogEnabled;

    /// <summary>
    /// UI language options.
    /// </summary>
    public ObservableCollection<string> Languages { get; } = [Loc.Instance.Get("Lang_System"), "Русский", "English"];

    [ObservableProperty]
    private int _selectedLanguageIndex;

    [ObservableProperty]
    private string _appVersion = "AmneziaGeo -";

    [ObservableProperty]
    private string _amneziaVersion = Loc.Instance.Get("MainVm_NotAvailable");

    [ObservableProperty]
    private string _newSourceKind = "geosite";

    [ObservableProperty]
    private string _newSourceUrl = string.Empty;

    [ObservableProperty]
    private bool _sourceKindLocked;

    [ObservableProperty]
    private bool _hasSources;

    private string _geoCategorySignature = string.Empty;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private bool _hasLogs;

    private IReadOnlyList<string> _logLines = [];

    [ObservableProperty]
    private int _logSeverity;

    /// <summary>
    /// On-disk log files offered in the viewer's file picker (agent rolls + routing log), newest first.
    /// </summary>
    public ObservableCollection<LogFileChoice> LogFiles { get; } = [];

    [ObservableProperty]
    private LogFileChoice? _selectedLogFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchSummary))]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchSummary))]
    private int _searchMatchCount;

    // Whether the view snaps to the live tail on each snapshot heartbeat.
    [ObservableProperty]
    private bool _logFollow = true;

    // Whether content exists before the loaded window (enables "load earlier").
    [ObservableProperty]
    private bool _logCanPageOlder;

    // Serializes log reads so overlapping heartbeat/user loads never interleave on the pipe.
    private readonly System.Threading.SemaphoreSlim _logReadGate = new(1, 1);

    // Byte offset of the oldest line currently loaded; the anchor for paging further back.
    private long? _logOldestOffset;

    // Once the file-backed viewer has loaded, the 300-line snapshot ring stops feeding the view.
    private bool _logViewerEngaged;

    // Guards against overlapping OpListLogs refreshes (heartbeats retry until the first one succeeds).
    private bool _logListBusy;

    // Tail window requested per read (bytes); the agent clamps it.
    private const int LogTailBytes = 262144;

    private static readonly JsonSerializerOptions LogJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Human-readable match count for the log search box; empty when no query is active.
    /// </summary>
    public string SearchSummary => string.IsNullOrWhiteSpace(SearchQuery)
        ? string.Empty
        : Loc.Instance.Get("MainVm_LogSearchMatches", SearchMatchCount);

    private bool _suppressSettingPush;

    private readonly UiPreferences _prefs;

    // Atomic per-item edit model (#143): aggregates the open item's edit scopes into IsEditing + Save/Cancel.
    private readonly EditController _editController = new();

    // Host-owned edit scopes for the config / profile sections' inline fields (#143), built in the ctor and
    // registered by RefreshEditScopes while that section is active. The _base* fields are the rename baselines.
    private DelegateEditScope? _configRenameScope;
    private DelegateEditScope? _sectionConfigScope;
    private DelegateEditScope? _profileRenameScope;
    private string _baseConfigRename = string.Empty;
    private string _baseProfileRename = string.Empty;

    /// <summary>
    /// ctor
    /// </summary>
    public MainWindowViewModel(AgentConnection connection, UiPreferences prefs)
    {
        _connection = connection;
        _prefs = prefs;
        _editController.EditingChanged += (_, _) => OnEditingChanged();
        _configRenameScope = new DelegateEditScope(
            () => !string.Equals(ConfigRename ?? string.Empty, _baseConfigRename, StringComparison.Ordinal),
            () => _baseConfigRename = ConfigRename ?? string.Empty,
            () => ConfigRename = _baseConfigRename,
            CommitConfigRenameAsync);
        _sectionConfigScope = new DelegateEditScope(
            () => IsCreatingSectionConfig,
            () => { },
            CancelSectionConfig,
            CommitSectionConfigAsync);
        _profileRenameScope = new DelegateEditScope(
            () => !string.Equals(ProfileRename ?? string.Empty, _baseProfileRename, StringComparison.Ordinal),
            () => _baseProfileRename = ProfileRename ?? string.Empty,
            () => ProfileRename = _baseProfileRename,
            CommitProfileRenameAsync);
        // Seed backing fields from prefs without echoing OnChanged.
        _isDark = prefs.IsDark;
        _settingsSection = prefs.SettingsSection;
        _selectedLanguageIndex = IndexForLanguage(prefs.Language);
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
    /// Profiles under the shell's name (profile = config × routing).
    /// </summary>
    public ObservableCollection<BalancerItemViewModel> Profiles => Balancers;

    public ObservableCollection<ProfileChoice> ProfileOptions { get; } = [ProfileChoice.None];

    public ObservableCollection<ConfigChoice> ConfigCatalogueOptions { get; } = [ConfigChoice.None];

    public ObservableCollection<RoutingListChoice> RoutingCatalogueOptions { get; } = [RoutingListChoice.None];

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
            2 => StatusLabels.Text(BoundStatus),
            1 => StatusLabels.Text(IsTunnelActive ? ConnectionStatus.Connecting : ConnectionStatus.Disconnecting),
            _ => StatusLabels.Text(ConnectionStatus.Disconnected),
        }
        : Loc.Instance.Get("MainVm_NoAgentConnection");

    public bool CanToggleConnection => IsConnected && !IsEditing && (IsTunnelActive || (ActiveProfile is { IsComplete: true }));

    private static readonly IBrush _circleBlue = new SolidColorBrush(Color.FromRgb(0x2A, 0x6F, 0xDB));
    private static readonly IBrush _circleBorderGray = new SolidColorBrush(Color.FromRgb(0xD9, 0xDD, 0xE6));
    private static readonly IBrush _circleBorderAmber = new SolidColorBrush(Color.FromRgb(0xF0, 0xD3, 0xA8));
    private static readonly IBrush _glyphGray = new SolidColorBrush(Color.FromRgb(0x7B, 0x81, 0x8D));
    private static readonly IBrush _glyphAmber = new SolidColorBrush(Color.FromRgb(0xE0, 0x90, 0x2F));
    private static readonly IBrush _textBlue = new SolidColorBrush(Color.FromRgb(0x1A, 0x50, 0xB0));
    private static readonly IBrush _textAmber = new SolidColorBrush(Color.FromRgb(0xB8, 0x72, 0x1F));
    private static readonly IBrush _textGray = new SolidColorBrush(Color.FromRgb(0x5B, 0x61, 0x6E));
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

    public bool ShowSelectConfigHint => ConnState == 0 && HasBalancers && ActiveProfile is not { IsComplete: true };

    public bool ShowNoProfilesYetHint => HasConfigs && !HasBalancers;

    public string ConnectPillContent => IsTunnelActive ? Loc.Instance.Get("MainVm_Disconnect") : Loc.Instance.Get("MainVm_Connect");

    public IBrush ConnectCircleBrush => ConnState == 2 ? _circleBlue : Brushes.White;

    public IBrush ConnectCircleBorderBrush => ConnState switch { 2 => Brushes.Transparent, 1 => _circleBorderAmber, _ => _circleBorderGray };

    public IBrush ConnectCircleForeground => ConnState switch { 2 => Brushes.White, 1 => _glyphAmber, _ => _glyphGray };

    public IBrush ConnectStatusBrush => ConnState switch { 2 => _textBlue, 1 => _textAmber, _ => _textGray };

    public IBrush ConnectHintBrush => _hintBrush;

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

    public bool IsProfileDetail => OpenProfile is not null;

    public bool ProfileNameMissing => string.IsNullOrWhiteSpace(ProfileRename);

    public bool ConfigNameMissing => string.IsNullOrWhiteSpace(ConfigRename);

    public bool SectionConfigNameMissing => string.IsNullOrWhiteSpace(SectionConfigName);

    public bool SectionConfigNameIsDefault =>
        string.IsNullOrWhiteSpace(SectionConfigName)
        || string.Equals(SectionConfigName, _sectionConfigDefaultName, StringComparison.Ordinal);

    public bool CanSaveSectionConfig =>
        VpnLinkCodec.TryDecode(SectionConfigText) is not null && !string.IsNullOrWhiteSpace(SectionConfigName);

    public bool IsConfigManage => OpenConfig is not null;

    public bool IsSettingsProfile => SettingsSection == "profile";

    public bool IsSettingsConfig => SettingsSection == "config";

    public bool IsSettingsRouting => SettingsSection == "routing";

    public bool IsSettingsGeneral => SettingsSection == "general";

    public bool IsSettingsLogs => SettingsSection == "logs";

    public bool IsSettingsSources => SettingsSection == "sources";

    /// <summary>
    /// Whether the rule editor is shown in the Routing settings section (a list is selected).
    /// </summary>
    public bool HasRoutingEditor => RoutingEditor is not null;

    /// <summary>
    /// Current theme label shown on the toggle button.
    /// </summary>
    public string ThemeLabel => IsDark ? Loc.Instance.Get("Theme_Dark") : Loc.Instance.Get("Theme_Light");

    public string UpdateVersionBadgeText => Loc.Instance.Get("Main_UpdateAvailableVersion", UpdateVersion);

    public string UpdateBannerText => Loc.Instance.Get("Main_UpdateBanner", UpdateVersion);

    public string GeoUpdateBannerText => Loc.Instance.Get("Main_GeoUpdateBanner", GeoUpdateCount);

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

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        var token = TokenForLanguageIndex(value);
        _prefs.Language = token;
        _prefs.Save();
        Loc.Instance.SetCulture(token);
    }

    private void OnCultureChanged()
    {
        // Refresh the localized "System" entry in the language combo.
        if (Languages.Count > 0)
        {
            Languages[0] = Loc.Instance.Get("Lang_System");
        }

        // Re-raise all computed labels on a language change.
        OnPropertyChanged(string.Empty);
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
        // Open the active profile when entering settings.
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

    // ---- Atomic per-item edit model (#143): a dirty item blocks navigation; the header Save/Cancel commit or
    // revert the whole item at once. Only the item-editor sections (profile / config / routing) participate;
    // general / logs / sources and theme / language stay instant. ----

    /// <summary>
    /// True while the open settings item holds an uncommitted change. Blocks navigation to other items /
    /// sections and shows the header Save / Cancel.
    /// </summary>
    public bool IsEditing => _editController.IsEditing;

    private bool CanSaveEdit => IsEditing;

    // A scope's dirtiness (or the active scope set) changed: refresh IsEditing and everything gated on it.
    private void OnEditingChanged()
    {
        OnPropertyChanged(nameof(IsEditing));
        SaveEditCommand.NotifyCanExecuteChanged();
        CancelEditCommand.NotifyCanExecuteChanged();
        CreateProfileCommand.NotifyCanExecuteChanged();
        NotifyCanToggleConnection();
    }

    // Re-point the edit controller at the scopes of the item open in the active section. Navigation is blocked
    // while editing, so this only runs from a clean state (section / item switch) or once a Save/Cancel settles.
    private void RefreshEditScopes()
    {
        switch (SettingsSection)
        {
            case "profile":
                // Config / routing selections (BalancerItemViewModel) commit first; rename LAST so the
                // config/routing ops still key by the old profile name.
                _editController.SetScopes(OpenProfile, _profileRenameScope);
                break;
            case "routing":
                _editController.SetScopes(RoutingEditor, RoutingSettings);
                break;
            case "config":
                // Creating a new config: only the import-form scope. Editing an existing config: the .conf
                // editor + transport + rename - rename LAST so the .conf/transport ops still key by the old name.
                if (IsCreatingSectionConfig)
                {
                    _editController.SetScopes(_sectionConfigScope);
                }
                else
                {
                    _editController.SetScopes(ConfigExport, ConfigTransport, _configRenameScope);
                }

                break;
            default:
                // Profile joins the model in a later stage; other sections stay instant (#143 scope).
                _editController.SetScopes();
                break;
        }
    }

    /// <summary>
    /// Commits every dirty scope of the open item through the agent. On success IsEditing clears and navigation
    /// unblocks; a rejected commit leaves the item dirty with its own status shown.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private async Task SaveEdit()
    {
        await _editController.SaveAsync();
    }

    /// <summary>
    /// Reverts every dirty scope of the open item to its last committed values, then discards an unsaved
    /// new-item draft (a brand-new routing list has no committed baseline to fall back to).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveEdit))]
    private void CancelEdit()
    {
        _editController.Cancel();

        if (RoutingEditor is { IsNew: true })
        {
            RoutingEditor = null;
            RoutingSettings = null;
            EditRoutingList = null;
        }
    }

    [RelayCommand]
    private async Task DeleteOpenConfig()
    {
        if (OpenConfig is null)
        {
            return;
        }

        ConfigDeleteStatus = string.Empty;
        var config = OpenConfig;

        // No open profile: remove from the catalogue.
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

        // Shared config: detach from this profile if others still use it.
        var sharedByOthers = Balancers.Any(b =>
            !ReferenceEquals(b, OpenProfile) && string.Equals(b.Config, config, StringComparison.Ordinal));
        if (sharedByOthers)
        {
            await SaveBalancerAsync(OpenProfile.Name, string.Empty);
            OpenConfig = null;
            return;
        }

        // Last user of the config: delete it from the catalogue.
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

    // Commit the open config's rename through the agent (#143 header Save). Keyed by the current name; on OK it
    // pins _pendingOpenConfig so the next snapshot re-opens the renamed row. An empty name is rejected (the item
    // stays dirty and the field shows its required-warning).
    private async Task<bool> CommitConfigRenameAsync()
    {
        var current = OpenConfig;
        if (current is null)
        {
            return true;
        }

        var next = (ConfigRename ?? string.Empty).Trim();
        if (next.Length == 0)
        {
            ConfigRenameStatus = Loc.Instance.Get("Main_RequiredEmptyWarning");
            return false;
        }

        if (string.Equals(next, current, StringComparison.Ordinal))
        {
            return true;
        }

        ConfigRenameStatus = string.Empty;
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpRenameConfig, [current, next]));
        if (ack.Ok)
        {
            _pendingOpenConfig = next;
            return true;
        }

        ConfigRenameStatus = ack.Message;
        return false;
    }

    // The rename field changed: re-evaluate the config item's dirtiness (no auto-save - the header Save commits).
    partial void OnConfigRenameChanged(string value)
    {
        _configRenameScope?.RaiseDirtyChanged();
    }

    partial void OnOpenConfigChanged(string? value)
    {
        ConfigDeleteStatus = string.Empty;
        // Set the rename baseline before the field so seeding it does not read as a dirty edit (#143).
        _baseConfigRename = value ?? string.Empty;
        ConfigRename = value ?? string.Empty;
        ConfigRenameStatus = string.Empty;
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

        var item = Configs.FirstOrDefault(c => string.Equals(c.Name, value, StringComparison.Ordinal));
        ConfigTransport = new ConfigTransportViewModel(_connection, value, item?.Endpoint ?? string.Empty, item?.UseWebSocket ?? false, item?.WebSocketHost ?? string.Empty, item?.WebSocketPort ?? 443, item?.Mtu ?? 0);
    }

    partial void OnSelectedCatalogueConfigChanged(ConfigChoice? value)
    {
        if (_suppressCatalogueConfig || value is null)
        {
            return;
        }

        if (IsCreatingSectionConfig)
        {
            CancelSectionConfig();
        }

        OpenConfig = value.IsReal ? value.Name : null;
    }

    partial void OnIsCreatingSectionConfigChanged(bool value)
    {
        SyncCatalogueConfig();
        RefreshEditScopes();
    }

    private void SyncCatalogueConfig()
    {
        _suppressCatalogueConfig = true;
        SelectedCatalogueConfig = OpenConfig is null
            ? ConfigChoice.None
            : ConfigCatalogueOptions.FirstOrDefault(o => o.IsReal && string.Equals(o.Name, OpenConfig, StringComparison.Ordinal)) ?? ConfigChoice.None;
        _suppressCatalogueConfig = false;
    }

    private void ReconcileConfigCatalogueOptions()
    {
        const int head = 1; // None occupies [0].
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

    partial void OnActiveProfileChanged(BalancerItemViewModel? oldValue, BalancerItemViewModel? newValue)
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
        if (e.PropertyName is nameof(BalancerItemViewModel.Config) or nameof(BalancerItemViewModel.IsComplete))
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
            ? Balancers.FirstOrDefault(b => string.Equals(b.Name, value.Identity, StringComparison.Ordinal))
            : null;
    }

    partial void OnOpenProfileChoiceChanged(ProfileChoice? value)
    {
        if (_suppressOpenChoice || value is null)
        {
            return;
        }

        OpenProfile = value.IsReal
            ? Balancers.FirstOrDefault(b => string.Equals(b.Name, value.Identity, StringComparison.Ordinal))
            : null;
    }

    private void SyncActiveProfileChoice()
    {
        _suppressActiveChoice = true;
        ActiveProfileChoice = ActiveProfile is null
            ? ProfileChoice.None
            : ProfileOptions.FirstOrDefault(o => o.IsReal && string.Equals(o.Identity, ActiveProfile.Name, StringComparison.Ordinal)) ?? ProfileChoice.None;
        _suppressActiveChoice = false;
    }

    private void SyncOpenProfileChoice()
    {
        _suppressOpenChoice = true;
        OpenProfileChoice = OpenProfile is null
            ? ProfileChoice.None
            : ProfileOptions.FirstOrDefault(o => o.IsReal && string.Equals(o.Identity, OpenProfile.Name, StringComparison.Ordinal)) ?? ProfileChoice.None;
        _suppressOpenChoice = false;
    }

    // The rename field changed: re-evaluate the profile item's dirtiness (no auto-save - the header Save commits).
    partial void OnProfileRenameChanged(string value)
    {
        _profileRenameScope?.RaiseDirtyChanged();
    }

    // Reset the option label back to the persisted name.
    private void ResetProfileOptionLabel(string identity)
    {
        var option = ProfileOptions.FirstOrDefault(
            o => o.IsReal && string.Equals(o.Identity, identity, StringComparison.Ordinal));
        if (option is not null && !string.Equals(option.Name, option.Identity, StringComparison.Ordinal))
        {
            option.Name = option.Identity;
        }
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

    // The Routing section combo pick: «— не выбрано —» closes the editor; a real list opens it for editing. A
    // new-list draft is opened by the «+ Новый список» button (#111), not from this combo.
    partial void OnSelectedCatalogueRoutingChanged(RoutingListChoice? value)
    {
        if (_suppressCatalogueRouting || value is null)
        {
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
    // the combo. A selected real list is reflected via EditRoutingList; a new-list draft has no combo entry
    // now (#111), so the combo reads «— не выбрано —» while it is being created below.
    partial void OnRoutingEditorChanged(RoutingListEditorViewModel? oldValue, RoutingListEditorViewModel? newValue)
    {
        SyncCatalogueRouting();
        RefreshEditScopes();
    }

    // The per-routing settings editor was (re)built or cleared: re-point the edit controller (#143).
    partial void OnRoutingSettingsChanged(RoutingSettingsViewModel? oldValue, RoutingSettingsViewModel? newValue)
    {
        RefreshEditScopes();
    }

    // Mirror the Routing section's state into its combo without echoing the pick back: a selected real list
    // shows its row, otherwise «— не выбрано —» (including while a new-list draft is being created).
    private void SyncCatalogueRouting()
    {
        _suppressCatalogueRouting = true;
        SelectedCatalogueRouting = EditRoutingList is null
            ? RoutingListChoice.None
            : RoutingCatalogueOptions.FirstOrDefault(o => o.IsReal && o.Id == EditRoutingList.Id) ?? RoutingListChoice.None;
        _suppressCatalogueRouting = false;
    }

    // Reconcile RoutingCatalogueOptions in place from RoutingLists: keep «— не выбрано —» at [0] and reconcile
    // the real (id) choices after it - replacing a renamed row so its label updates. A following
    // SyncCatalogueRouting re-selects by id, so a replace never strands the selection.
    private void ReconcileRoutingCatalogueOptions()
    {
        const int head = 1; // None occupies [0].
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
            // Discard any live rename preview on the profile we are leaving: snap its combo label back to the
            // persisted name so an unsaved (or agent-rejected) edit does not linger in the picker (#110). Only
            // on a real identity change - a background re-bind of the same profile keeps the in-progress name.
            if (oldValue is not null)
            {
                ResetProfileOptionLabel(oldValue.Name);
            }

            // Set the rename baseline before the field so seeding it does not read as a dirty edit (#143).
            _baseProfileRename = newValue?.Name ?? string.Empty;
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

        // Re-point the edit model at the newly-open profile (its config/routing selections + the rename field).
        RefreshEditScopes();

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

    // The config's transport / .conf editors were (re)built or cleared: re-point the edit controller (#143).
    partial void OnConfigTransportChanged(ConfigTransportViewModel? oldValue, ConfigTransportViewModel? newValue)
    {
        RefreshEditScopes();
    }

    partial void OnConfigExportChanged(ExportDialogViewModel? oldValue, ExportDialogViewModel? newValue)
    {
        RefreshEditScopes();
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

        // Opening the log section loads the on-disk files at once, rather than waiting for the next heartbeat.
        if (value == "logs")
        {
            _ = RefreshLogFilesAsync();
        }

        // Re-point the edit model at the new section's open item (only profile/config/routing carry scopes).
        RefreshEditScopes();
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
            // Drop the file-backed log viewer state so a reconnect re-lists cleanly (a file that rolled away
            // during the outage is not re-read by its stale name) and the snapshot ring feeds the view again
            // until the on-disk listing succeeds.
            _logViewerEngaged = false;
            _logOldestOffset = null;
            LogCanPageOlder = false;
            LogFiles.Clear();
            SelectedLogFile = null;
            _logLines = [];
            HasLogs = false;
            RebuildLogText();
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

        UpdateLogView(snapshot);
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

    // Rebuilds the journal text from the raw lines applying the severity filter and the search query, newest
    // first so the latest activity stays visible at the top without scrolling.
    private void RebuildLogText()
    {
        var threshold = LogSeverity;
        var query = SearchQuery;
        var hasQuery = !string.IsNullOrWhiteSpace(query);

        var shown = new List<string>();
        foreach (var line in _logLines)
        {
            if (threshold > 0 && LineRank(line) < threshold)
            {
                continue;
            }

            if (hasQuery && !line.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            shown.Add(line);
        }

        SearchMatchCount = hasQuery ? shown.Count : 0;
        if (shown.Count == 0)
        {
            LogText = string.Empty;
            return;
        }

        shown.Reverse();
        LogText = string.Join('\n', shown);
    }

    // Feeds the log view. Before the file-backed viewer is engaged the 300-line snapshot ring drives it (so
    // the section shows something instantly); once engaged, the on-disk file is the source of truth and the
    // heartbeat re-reads the live tail while the log section is open, follow is on, and the newest file is
    // selected.
    private void UpdateLogView(StatusSnapshot snapshot)
    {
        if (!_logViewerEngaged)
        {
            // Not reading files yet: the 300-line ring drives the view. In the log section, also try to engage
            // the file-backed viewer - it only takes over once OpListLogs actually succeeds, so a failed listing
            // leaves the ring showing instead of a blank panel.
            _logLines = snapshot.Logs ?? [];
            HasLogs = _logLines.Count > 0;
            RebuildLogText();
            if (IsSettingsLogs)
            {
                _ = RefreshLogFilesAsync();
            }

            return;
        }

        if (IsSettingsLogs && LogFollow && LogFiles.Count > 0
            && ReferenceEquals(SelectedLogFile, LogFiles[0]) && _logReadGate.CurrentCount > 0)
        {
            _ = LoadLogTailAsync();
        }
    }

    // Loads (or refreshes) the list of on-disk log files and re-reads the tail of the selected one. Engages
    // the file-backed viewer only once the listing succeeds, so a transient IPC failure never strands the
    // viewer with the snapshot ring disabled; heartbeats retry it while the log section stays open.
    private async Task RefreshLogFilesAsync()
    {
        if (_logListBusy)
        {
            return;
        }

        _logListBusy = true;
        try
        {
            IpcAck ack;
            try
            {
                ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpListLogs, []));
            }
            catch
            {
                return;
            }

            if (!ack.Ok)
            {
                return;
            }

            List<LogFileChoice>? metas;
            try
            {
                metas = JsonSerializer.Deserialize<List<LogFileChoice>>(ack.Message, LogJson);
            }
            catch (JsonException)
            {
                return;
            }

            if (metas is null || metas.Count == 0)
            {
                return;
            }

            // The file-backed source is now available: flip the latch so the ring stops feeding the view.
            _logViewerEngaged = true;

            var previous = SelectedLogFile?.Name;
            LogFiles.Clear();
            foreach (var meta in metas)
            {
                LogFiles.Add(meta);
            }

            // Keep the same file selected across refreshes; fall back to the newest when it has rolled away.
            var target = (previous is not null ? LogFiles.FirstOrDefault(f => f.Name == previous) : null)
                ?? LogFiles[0];
            var before = SelectedLogFile;
            SelectedLogFile = target;

            // A value-equal reselect (byte-identical newest file) short-circuits the [ObservableProperty]
            // setter, so OnSelectedLogFileChanged never fires; reload the tail explicitly so a section re-open
            // reliably refreshes it.
            if (ReferenceEquals(SelectedLogFile, before))
            {
                _logOldestOffset = null;
                LogCanPageOlder = false;
                await LoadLogTailAsync();
            }
        }
        finally
        {
            _logListBusy = false;
        }
    }

    // Reads the selected log file over IPC: the live tail by default, or the window ending at the oldest
    // loaded offset when paging older. Serialized so heartbeat and user loads never interleave on the pipe.
    private async Task LoadLogTailAsync(bool older = false)
    {
        var file = SelectedLogFile;
        if (file is null)
        {
            return;
        }

        // Paging older needs an anchor; without one there is nothing before the loaded window.
        if (older && _logOldestOffset is not > 0)
        {
            return;
        }

        await _logReadGate.WaitAsync();
        try
        {
            var args = new List<string>
            {
                file.Name,
                LogTailBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (older)
            {
                args.Add(_logOldestOffset!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            IpcAck ack;
            try
            {
                ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpReadLog, args));
            }
            catch
            {
                return;
            }

            if (!ack.Ok)
            {
                return;
            }

            LogTailPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<LogTailPayload>(ack.Message, LogJson);
            }
            catch (JsonException)
            {
                return;
            }

            if (payload is null)
            {
                return;
            }

            if (older)
            {
                _logLines = [.. payload.Lines, .. _logLines];
            }
            else
            {
                _logLines = payload.Lines;
            }

            _logOldestOffset = payload.FirstOffset;
            LogCanPageOlder = payload.Truncated;
            HasLogs = _logLines.Count > 0;
            RebuildLogText();
        }
        finally
        {
            _logReadGate.Release();
        }
    }

    [RelayCommand]
    private async Task LoadOlderLog()
    {
        // Browsing history: stop the heartbeat from snapping back to the live tail and dropping what we page in.
        LogFollow = false;
        await LoadLogTailAsync(older: true);
    }

    partial void OnSelectedLogFileChanged(LogFileChoice? value)
    {
        // A different file (or a reselect) starts from the live tail again.
        _logOldestOffset = null;
        LogCanPageOlder = false;
        if (value is null)
        {
            _logLines = [];
            HasLogs = false;
            RebuildLogText();
            return;
        }

        _ = LoadLogTailAsync();
    }

    partial void OnSearchQueryChanged(string value)
    {
        RebuildLogText();
    }

    partial void OnLogFollowChanged(bool value)
    {
        // Re-enabling follow snaps back to the live tail (dropping any paged-older history).
        if (value)
        {
            _ = LoadLogTailAsync();
        }
    }

    // OpReadLog ack payload: a bounded window of a log file, oldest line first.
    private sealed record LogTailPayload(
        IReadOnlyList<string> Lines,
        long FirstOffset,
        long FileSize,
        bool Truncated);

    // Severity rank of a rendered log line: trace/debug = 0, info = 1, warn = 2, error/fatal = 3. Two formats
    // reach here - the agent journal ring ("HH:mm:ss LVL message", level at offset 9) and the on-disk file the
    // viewer reads ("<iso timestamp> [LVL] message", level bracketed; ISO lines start "yyyy-"). Unparseable
    // lines rank as info so they are never hidden by a relaxed filter yet drop out under the errors-only view.
    private static int LineRank(string line)
    {
        string code;
        if (line.Length >= 5 && line[4] == '-')
        {
            var open = line.IndexOf('[');
            code = open >= 0 && open + 4 < line.Length && line[open + 4] == ']'
                ? line.Substring(open + 1, 3)
                : string.Empty;
        }
        else
        {
            code = line.Length >= 12 ? line.Substring(9, 3) : string.Empty;
        }

        return code switch
        {
            "TRC" or "DBG" or "VRB" => 0,
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
                existing = new BalancerItemViewModel(SaveBalancerAsync, AssignRoutingAsync, SelectProfileAsync, ToggleProfileConnectionAsync, RemoveConfigAsync);
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

    // Reconcile ProfileOptions in place from Balancers: keep «— не выбрано —» at [0] and reconcile the real
    // choices after it - dropping removed profiles, adding new ones, and reordering to match. Options are
    // matched by Identity (the persisted profile name), which stays stable across a live-typed rename, so an
    // in-progress rename preview (#110) is not clobbered by a snapshot push. Editing in place (rather than
    // Clear + rebuild) keeps the None / existing choice instances alive so a bound ComboBox's selection is not
    // reset by the refresh.
    private void ReconcileProfileOptions()
    {
        const int head = 1; // None occupies [0].
        var present = Balancers.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = ProfileOptions.Count - 1; i >= head; i--)
        {
            if (!present.Contains(ProfileOptions[i].Identity))
            {
                ProfileOptions.RemoveAt(i);
            }
        }

        for (var i = 0; i < Balancers.Count; i++)
        {
            var name = Balancers[i].Name;
            var slot = head + i;
            var existing = ProfileOptions.Skip(head).FirstOrDefault(o => string.Equals(o.Identity, name, StringComparison.Ordinal));
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
        // Order: «Полный туннель» (None) then the saved lists. Creating a list is the «+ Новый список» button
        // in the Routing section, not a combo entry (#111).
        var options = new List<RoutingListChoice> { RoutingListChoice.None };
        foreach (var entry in entries)
        {
            options.Add(new RoutingListChoice(entry.Id, entry.Name));
        }

        return options;
    }

    // The config catalogue for a profile's combo. Order: «— не выбрано —» (None) then every config (shared /
    // reusable across profiles). Creating a config is the «+ Новая конфигурация» button in the Config section,
    // not a combo entry (#111).
    private IReadOnlyList<ConfigChoice> BuildConfigOptions()
    {
        var options = new List<ConfigChoice> { ConfigChoice.None };
        foreach (var name in _configNames)
        {
            options.Add(new ConfigChoice(name));
        }

        return options;
    }

    // "+ Профиль": create a profile pre-assigned a configuration (#113), then auto-open it so it can be tuned.
    // The button is disabled while the catalogue is empty (a profile needs a config to dial), so there is
    // always a config to assign here: prefer the current working profile's config, else the first in the
    // catalogue.
    // Disabled while the catalogue is empty (a profile needs a config) and while another item is being edited
    // (#143), so creating a new profile cannot abandon an unsaved edit.
    private bool CanCreateProfile => HasConfigs && !IsEditing;

    [RelayCommand(CanExecute = nameof(CanCreateProfile))]
    private async Task CreateProfile()
    {
        var config = ActiveProfile is { Config.Length: > 0 } active
            ? active.Config
            : _configNames.FirstOrDefault() ?? string.Empty;

        var name = UniqueProfileName();
        string[] args = config.Length > 0 ? [name, config] : [name];
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpAddBalancer, args));
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

    // A unique default name for a new config / routing list, mirroring UniqueProfileName (#117): a new item is
    // pre-named so the required-name field is never empty on open; "<base>", then "<base> 2", "<base> 3"…
    private static string UniqueDefaultName(string baseName, IEnumerable<string> taken)
    {
        var existing = taken.ToHashSet(StringComparer.Ordinal);
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

    private string UniqueConfigName()
    {
        // Include a just-imported config that has not yet landed in _configNames (it arrives on the next
        // snapshot via _pendingOpenConfig), so re-opening the create form before the snapshot does not
        // propose the same default a second time (#117 review).
        var taken = _pendingOpenConfig is { } pending ? _configNames.Append(pending) : _configNames;
        return UniqueDefaultName(Loc.Instance.Get("MainVm_NewConfigDefaultName"), taken);
    }

    private string UniqueRoutingListName()
    {
        // Include the name still held by the open editor: a just-saved new list lags RoutingLists by one
        // snapshot, so without this a rapid second «+ Новый список» would propose the same default again
        // and persist two identically-named lists (#117 review).
        var taken = RoutingLists.Select(r => r.Name);
        if (RoutingEditor is { } editor)
        {
            taken = taken.Append(editor.Name);
        }

        return UniqueDefaultName(Loc.Instance.Get("MainVm_NewListDefaultName"), taken);
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
    // without a profile). The file / QR / camera pickers and the editor dialog are window concerns that fill
    // SectionConfigText; the header Save (#143) imports a recognised config with a (required) name and opens it
    // for management, and the header Cancel discards the draft. ---

    [RelayCommand]
    private void BeginSectionConfig()
    {
        // Close any open config first so only the create form shows (the redirect from a profile's picker can
        // arrive with a config still open), and so the combo lands on «+ Новая конфигурация», not a real row.
        OpenConfig = null;
        // Pre-fill a unique default name (like a new profile, #117) so the required-name field is never empty
        // on open; the user can accept it or type over it. Clearing it turns the box red and blocks the save.
        // Remember the default so an import can still overwrite it with the config's own name (SectionConfigNameIsDefault).
        _sectionConfigDefaultName = UniqueConfigName();
        SectionConfigName = _sectionConfigDefaultName;
        SectionConfigText = string.Empty;
        SectionConfigStatus = string.Empty;
        IsCreatingSectionConfig = true;
    }

    // Discards the create-form draft. Called by the header Cancel (#143 revert) and on disconnect.
    private void CancelSectionConfig()
    {
        IsCreatingSectionConfig = false;
        SectionConfigName = string.Empty;
        SectionConfigText = string.Empty;
        SectionConfigStatus = string.Empty;
    }

    // Header Save (#143) for the create form: import the recognised config, or fail (kept dirty) if the text is
    // not a valid config or the required name is missing.
    private async Task<bool> CommitSectionConfigAsync()
    {
        if (!CanSaveSectionConfig)
        {
            SectionConfigStatus = Loc.Instance.Get("MainVm_ConfigNotRecognized");
            return false;
        }

        return await SaveSectionConfig();
    }

    // Adds the recognised config to the catalogue and opens it (via _pendingOpenConfig on the next snapshot).
    // Returns whether the import succeeded.
    private async Task<bool> SaveSectionConfig()
    {
        // Re-entrancy guard: the import below is awaited while the form is still open and populated; guard against
        // a second overlapping save re-entering here and importing the config twice (#118 review). UI-thread only.
        if (_sectionConfigSaving)
        {
            return false;
        }

        var imported = VpnLinkCodec.TryDecode(SectionConfigText);
        if (imported is null)
        {
            SectionConfigStatus = Loc.Instance.Get("MainVm_ConfigNotRecognized");
            return false;
        }

        var name = !string.IsNullOrWhiteSpace(SectionConfigName)
            ? SectionConfigName.Trim()
            : (string.IsNullOrWhiteSpace(imported.Name) ? "config" : imported.Name!.Trim());

        _sectionConfigSaving = true;
        try
        {
            var ack = await ImportConfigAsync(name, imported.ConfText);
            if (!ack.Ok)
            {
                SectionConfigStatus = ack.Message;
                return false;
            }

            IsCreatingSectionConfig = false;
            SectionConfigName = string.Empty;
            SectionConfigText = string.Empty;
            SectionConfigStatus = string.Empty;
            // Open the just-imported config once its row lands in the next snapshot, so the transport editor
            // seeds from the real config row rather than all-defaults (the row is not in Configs yet here).
            _pendingOpenConfig = name;
            return true;
        }
        finally
        {
            _sectionConfigSaving = false;
        }
    }

    // --- Routing settings section: standalone list CRUD (independent of any profile). ---

    // "+ Новый список": show a fresh create-editor; per-routing settings are built once it is first saved.
    [RelayCommand]
    private void CreateRoutingList()
    {
        // Clear the selected list first: setting RoutingEditor below fires OnRoutingEditorChanged ->
        // SyncCatalogueRouting, which mirrors EditRoutingList into the combo, so it must already be null for
        // the combo to read «— не выбрано —» while the new draft is being created (rather than the list that
        // was open before).
        RoutingSettings = null;
        EditRoutingList = null;
        var editor = new RoutingListEditorViewModel(_connection, OnSectionRoutingEditorSaved);
        // Pre-fill a unique default name (like a new profile, #117) so the required-name field is never empty
        // on open. Set before LoadAsync, while the editor still suppresses auto-save, so seeding the name does
        // not schedule a save (a nameless-or-ruleless list is not persisted anyway); clearing it later turns
        // the box red and warns that changes will not be saved.
        editor.Name = UniqueRoutingListName();
        RoutingEditor = editor;
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

    // Commit the open profile's rename through the agent (#143 header Save). On OK the live instance adopts the
    // new name so the next snapshot reconciles it in place (SyncBalancers matches by name) instead of dropping
    // the row. An empty name is rejected (the item stays dirty); a refused rename shows why and reverts the
    // combo label to the persisted name.
    private async Task<bool> CommitProfileRenameAsync()
    {
        var profile = OpenProfile;
        if (profile is null)
        {
            return true;
        }

        var next = (ProfileRename ?? string.Empty).Trim();
        if (next.Length == 0)
        {
            ProfileRenameStatus = Loc.Instance.Get("Main_RequiredEmptyWarning");
            return false;
        }

        if (string.Equals(next, profile.Name, StringComparison.Ordinal))
        {
            return true;
        }

        ProfileRenameStatus = string.Empty;
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpRenameProfile, [profile.Name, next]));
        if (ack.Ok)
        {
            profile.Name = next;
            return true;
        }

        ProfileRenameStatus = ack.Message;
        ResetProfileOptionLabel(profile.Name);
        return false;
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
