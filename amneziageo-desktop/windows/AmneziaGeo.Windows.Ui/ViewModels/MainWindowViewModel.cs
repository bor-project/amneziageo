using Avalonia.Threading;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Top-level composer: hosts the per-screen view-models (home / profile / config / routing / sources / logs /
/// general), owns navigation + the settings-section rail, coordinates the atomic per-item edit model (#143),
/// and fans the agent snapshot out to each screen.
/// </summary>
internal sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;
    private readonly UiPreferences _prefs;

    // Atomic per-item edit model (#143): aggregates the open item's edit scopes into IsEditing + Save/Cancel.
    // The scopes live on the child screen view-models (Config / Profile / Routing); the shell registers them
    // via RefreshEditScopes while their section is active.
    private readonly EditController _editController = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoProfilesYetHint))]
    private bool _hasConfigs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoProfilesYetHint))]
    private bool _hasProfiles;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome))]
    [NotifyPropertyChangedFor(nameof(IsSettings))]
    private string _nav = "home";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsProfile))]
    [NotifyPropertyChangedFor(nameof(IsSettingsConfig))]
    [NotifyPropertyChangedFor(nameof(IsSettingsRouting))]
    [NotifyPropertyChangedFor(nameof(IsSettingsGeneral))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSources))]
    [NotifyPropertyChangedFor(nameof(IsSettingsLogs))]
    private string _settingsSection = "profile";

    /// <summary>
    /// ctor
    /// </summary>
    public MainWindowViewModel(AgentConnection connection, UiPreferences prefs)
    {
        _connection = connection;
        _prefs = prefs;
        Logs = new LogsViewModel(connection);
        General = new GeneralViewModel(connection, prefs);
        Config = new ConfigViewModel(this, connection);
        Profile = new ProfileViewModel(this, connection);
        Routing = new RoutingViewModel(this, connection);
        Home = new ConnectionViewModel(this, connection, prefs);
        Sources = new SourcesViewModel(connection, Home.ShowNotice, () => { _ = Routing.RoutingEditor?.RefreshSuggestionsAsync(); });
        _editController.EditingChanged += (_, _) => OnEditingChanged();
        // Seed backing field from prefs without echoing OnChanged.
        _settingsSection = prefs.SettingsSection;
        _connection.Connected += OnConnected;
        _connection.Disconnected += OnDisconnected;
        _connection.SnapshotReceived += OnSnapshot;
    }

    /// <summary>
    /// The connection used to talk to the agent.
    /// </summary>
    public AgentConnection Connection => _connection;

    /// <summary>
    /// Home screen: the connection card, tray-icon colour, and the notice banner.
    /// </summary>
    public ConnectionViewModel Home { get; }

    /// <summary>
    /// Logs screen.
    /// </summary>
    public LogsViewModel Logs { get; }

    /// <summary>
    /// General screen: theme, language, version, and app self-update.
    /// </summary>
    public GeneralViewModel General { get; }

    /// <summary>
    /// Config screen: the shared configuration catalogue and its editors.
    /// </summary>
    public ConfigViewModel Config { get; }

    /// <summary>
    /// Profile screen: the profile catalogue and the open-profile editor.
    /// </summary>
    public ProfileViewModel Profile { get; }

    /// <summary>
    /// Routing screen.
    /// </summary>
    public RoutingViewModel Routing { get; }

    /// <summary>
    /// Geo sources screen.
    /// </summary>
    public SourcesViewModel Sources { get; }

    /// <summary>
    /// Whether the Home (profiles) view is shown.
    /// </summary>
    public bool IsHome => Nav == "home";

    /// <summary>
    /// Whether the Settings view is shown (opened via the gear button).
    /// </summary>
    public bool IsSettings => Nav == "settings";

    public bool ShowNoProfilesYetHint => HasConfigs && !HasProfiles;

    public bool IsSettingsProfile => SettingsSection == "profile";

    public bool IsSettingsConfig => SettingsSection == "config";

    public bool IsSettingsRouting => SettingsSection == "routing";

    public bool IsSettingsGeneral => SettingsSection == "general";

    public bool IsSettingsLogs => SettingsSection == "logs";

    public bool IsSettingsSources => SettingsSection == "sources";

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
        return Config.ConfigNames;
    }

    [RelayCommand]
    private void NavHome()
    {
        Nav = "home";
        Profile.OpenProfile = null;
    }

    [RelayCommand]
    private void NavSettings()
    {
        // Open the active profile when entering settings.
        if (Home.ActiveProfile is not null)
        {
            Profile.OpenProfile = Home.ActiveProfile;
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

    /// <summary>
    /// Navigation lock: set while editing (a pending edit OR a pending deletion is a dirty scope, so both feed
    /// IsEditing). The back arrow and the section rail stay locked until the header Save/Cancel resolve it.
    /// </summary>
    public bool NavLocked => IsEditing;

    // A scope's dirtiness (or the active scope set) changed: refresh IsEditing and everything gated on it.
    private void OnEditingChanged()
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(NavLocked));
        Config.NotifyIsEditingChanged();
        Profile.NotifyIsEditingChanged();
        Routing.NotifyIsEditingChanged();
        Home.NotifyIsEditingChanged();
        SaveEditCommand.NotifyCanExecuteChanged();
        CancelEditCommand.NotifyCanExecuteChanged();
    }

    // Re-point the edit controller at the scopes of the item open in the active section. Navigation is blocked
    // while editing, so this only runs from a clean state (section / item switch) or once a Save/Cancel settles.
    internal void RefreshEditScopes()
    {
        switch (SettingsSection)
        {
            case "profile":
                // Config / routing selections (ProfileItemViewModel) commit first; rename LAST so the
                // config/routing ops still key by the old profile name.
                _editController.SetScopes(Profile.OpenProfile, Profile.RenameScope);
                break;
            case "routing":
                _editController.SetScopes(Routing.RoutingEditor, Routing.RoutingSettings);
                break;
            case "config":
                // Creating a new config: only the import-form scope. Editing an existing config: the .conf
                // editor + transport + rename - rename LAST so the .conf/transport ops still key by the old name.
                if (Config.IsCreatingSectionConfig)
                {
                    _editController.SetScopes(Config.SectionConfigScope);
                }
                else
                {
                    _editController.SetScopes(Config.ConfigExport, Config.ConfigTransport, Config.RenameScope);
                }

                break;
            default:
                // General / logs / sources stay instant - no edit scopes (#143).
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

        if (Routing.RoutingEditor is { IsNew: true })
        {
            Routing.CancelNewDraft();
        }
    }

    // A name is taken if any config OR profile already uses it: the agent enforces one shared namespace
    // (AgentStatusBroker rename/copy checks configRepo.ExistsAsync || store.GetProfileAsync). Ordinal, so this
    // is never STRICTER than the agent - a case-variant it would still reject just falls through to the server,
    // keeping the check a safe best-effort. Call only after confirming the new name differs from the current one.
    internal bool IsNameTaken(string name) =>
        Config.ConfigNames.Any(n => string.Equals(n, name, StringComparison.Ordinal))
        || Profile.Profiles.Any(b => string.Equals(b.Name, name, StringComparison.Ordinal));

    // Persist the selected settings section (#51) whenever it changes.
    partial void OnSettingsSectionChanged(string value)
    {
        _prefs.SettingsSection = value;
        _prefs.Save();

        // Changing section disarms any pending delete confirmation AND clears any blocked-delete reason line in
        // the section being left, so a stale red error does not linger on return (#3/#4).
        Config.ConfigDeletePending = false;
        Profile.ProfileDeletePending = false;
        Routing.RoutingDeletePending = false;
        Config.ConfigDeleteStatus = string.Empty;
        Profile.ProfileDeleteStatus = string.Empty;
        Routing.RoutingDeleteStatus = string.Empty;

        // Opening the log section loads the on-disk files at once, rather than waiting for the next heartbeat.
        Logs.SetActive(value == "logs");

        // Re-point the edit model at the new section's open item (only profile/config/routing carry scopes).
        RefreshEditScopes();
    }

    private void OnConnected()
    {
        Dispatcher.UIThread.Post(Home.SetConnected);
    }

    private void OnDisconnected()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Home.Reset();
            Profile.Reset();
            Config.Reset();
            Routing.Reset();
            Sources.Reset();
            Logs.Reset();
            HasConfigs = false;
            HasProfiles = false;
        });
    }

    private void OnSnapshot(StatusSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => Apply(snapshot));
    }

    private void Apply(StatusSnapshot snapshot)
    {
        Config.Apply(snapshot.Configs);
        Routing.Apply(snapshot);
        Sources.Apply(snapshot);
        Profile.Apply(snapshot.Profiles, snapshot.RoutingLists ?? []);
        HasConfigs = Config.Configs.Count > 0;
        HasProfiles = Profile.Profiles.Count > 0;
        Profile.NotifyHostFlagsChanged();
        Home.NotifyHostFlagsChanged();
        // The connection card matches the agent's target against the freshly-reconciled profile rows, so it
        // runs after Profile.Apply.
        Home.Apply(snapshot);
        General.Apply(snapshot);
        Logs.Apply(snapshot);
    }
}
