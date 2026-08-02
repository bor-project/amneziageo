using System.ComponentModel;
using Avalonia.Threading;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Top-level composer: hosts the per-screen view-models (connection / profile / config / routing / sources /
/// logs / general), owns the settings-section rail, and fans the agent snapshot out to each screen.
/// </summary>
internal sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IAgentConnection _connection;
    private readonly UiPreferences _prefs;

    // The footer reconnect prompt was put off; a new restart requirement or the next section save brings it back.
    private bool _reconnectDeferred;

    // Below this window width the settings screen drops the side-by-side rail + content for a single-column
    // master-detail drilldown. Above it the columns keep their MinWidth without overflow.
    private const double CompactBreakpoint = 760;

    /// <summary>
    /// Whether the app is showing a window ("settings") or running a windowless background update ("none"),
    /// carried into the installer as UPDATEORIGIN.
    /// </summary>
    public string CurrentSurface { get; set; } = "settings";

    /// <summary>
    /// The open view as a resume token ("home" or "settings/&lt;section&gt;"), carried into the installer as
    /// UPDATEVIEW so the relaunch after a self-update lands where the user left off.
    /// </summary>
    public string CurrentView => Nav == "settings" ? "settings/" + SettingsSection : "home";

    /// <summary>
    /// In-window view: "home" (connect screen) or "settings" (section console). The gear opens settings, the
    /// back arrow returns home.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome))]
    [NotifyPropertyChangedFor(nameof(IsSettings))]
    [NotifyPropertyChangedFor(nameof(ShowRail))]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    [NotifyPropertyChangedFor(nameof(ShowSplitter))]
    [NotifyPropertyChangedFor(nameof(AppUpdateBannerVisible))]
    [NotifyPropertyChangedFor(nameof(ReconnectPromptInSection))]
    [NotifyPropertyChangedFor(nameof(ShowReconnectBar))]
    private string _nav = "home";

    /// <summary>
    /// Current window width, fed from the view; drives the compact / wide layout switch.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompact))]
    [NotifyPropertyChangedFor(nameof(IsHorizontalHome))]
    [NotifyPropertyChangedFor(nameof(IsSectionDetail))]
    [NotifyPropertyChangedFor(nameof(ShowRail))]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    [NotifyPropertyChangedFor(nameof(ShowSplitter))]
    [NotifyPropertyChangedFor(nameof(ReconnectPromptInSection))]
    [NotifyPropertyChangedFor(nameof(ShowReconnectBar))]
    private double _windowWidth = 987;

    /// <summary>
    /// Current view height, used to restack the home controls on short landscape screens.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHorizontalHome))]
    private double _windowHeight = 610;

    /// <summary>
    /// In compact mode, whether a section detail is open (true) or the section rail is shown (false).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRail))]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    [NotifyPropertyChangedFor(nameof(IsSectionDetail))]
    [NotifyPropertyChangedFor(nameof(ReconnectPromptInSection))]
    [NotifyPropertyChangedFor(nameof(ShowReconnectBar))]
    private bool _settingsDetailOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoProfilesYetHint))]
    private bool _hasConfigs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoProfilesYetHint))]
    private bool _hasProfiles;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsProfile))]
    [NotifyPropertyChangedFor(nameof(IsSettingsConfig))]
    [NotifyPropertyChangedFor(nameof(IsSettingsRouting))]
    [NotifyPropertyChangedFor(nameof(IsSettingsGeneral))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSources))]
    [NotifyPropertyChangedFor(nameof(IsSettingsLogs))]
    [NotifyPropertyChangedFor(nameof(AppUpdateBannerVisible))]
    [NotifyPropertyChangedFor(nameof(ReconnectPromptInSection))]
    [NotifyPropertyChangedFor(nameof(ShowReconnectBar))]
    private string _settingsSection = "profile";

    /// <summary>
    /// ctor
    /// </summary>
    public MainWindowViewModel(IAgentConnection connection, UiPreferences prefs)
    {
        _connection = connection;
        _prefs = prefs;
        Diagnostics = new DiagnosticsViewModel(connection);
        General = new GeneralViewModel(this, connection, prefs);
        Config = new ConfigViewModel(this, connection);
        Profile = new ProfileViewModel(this, connection);
        Routing = new RoutingViewModel(this, connection);
        Home = new ConnectionViewModel(this, connection, prefs);
        Sources = new SourcesViewModel(connection, () => { _ = Routing.RoutingEditor?.RefreshSuggestionsAsync(); });
        // Seed backing field from prefs without echoing OnChanged.
        _settingsSection = prefs.SettingsSection;
        UpdateActiveSection();
        General.PropertyChanged += OnGeneralPropertyChanged;
        Config.PropertyChanged += OnSectionBarChanged;
        Profile.PropertyChanged += OnSectionBarChanged;
        Routing.PropertyChanged += OnSectionBarChanged;
        _connection.Connected += OnConnected;
        _connection.Disconnected += OnDisconnected;
        _connection.SnapshotReceived += OnSnapshot;
    }

    /// <summary>
    /// The connection used to talk to the agent.
    /// </summary>
    public IAgentConnection Connection => _connection;

    /// <summary>
    /// Home screen: the connection card, tray-icon colour, and the notice banner.
    /// </summary>
    public ConnectionViewModel Home { get; }

    /// <summary>
    /// Diagnostics screen: the agent log and the runtime configuration.
    /// </summary>
    public DiagnosticsViewModel Diagnostics { get; }

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

    public bool ShowNoProfilesYetHint => HasConfigs && !HasProfiles;

    /// <summary>
    /// Whether the home (connect) view is shown.
    /// </summary>
    public bool IsHome => Nav == "home";

    /// <summary>
    /// Whether the settings view is shown (opened via the gear button).
    /// </summary>
    public bool IsSettings => Nav == "settings";

    /// <summary>
    /// Whether the window is narrow enough for the compact single-column drilldown.
    /// </summary>
    public bool IsCompact => WindowWidth < CompactBreakpoint;

    /// <summary>
    /// Whether the short, wide home surface should place its controls side by side.
    /// </summary>
    public bool IsHorizontalHome => WindowHeight < 520 && WindowWidth >= CompactBreakpoint;

    /// <summary>
    /// Whether a section detail is open in compact mode; the header then shows the section name.
    /// </summary>
    public bool IsSectionDetail => IsCompact && SettingsDetailOpen;

    /// <summary>
    /// Whether the settings section rail is shown: always in wide mode, and in compact mode only when no section
    /// detail is open.
    /// </summary>
    public bool ShowRail => IsSettings && (!IsCompact || !SettingsDetailOpen);

    /// <summary>
    /// Whether the settings content pane is shown: always in wide mode, and in compact mode only when a section
    /// detail is open.
    /// </summary>
    public bool ShowContent => IsSettings && (!IsCompact || SettingsDetailOpen);

    /// <summary>
    /// Whether the rail / content splitter is shown (wide mode only).
    /// </summary>
    public bool ShowSplitter => IsSettings && !IsCompact;

    public bool IsSettingsProfile => SettingsSection == "profile";

    public bool IsSettingsConfig => SettingsSection == "config";

    public bool IsSettingsRouting => SettingsSection == "routing";

    public bool IsSettingsGeneral => SettingsSection == "general";

    public bool IsSettingsLogs => SettingsSection == "logs";

    public bool IsSettingsSources => SettingsSection == "sources";

    /// <summary>
    /// Whether the floating app-update banner shows. Hidden only on the settings General page, which already
    /// carries the update section (#186); shown on home and every other settings section.
    /// </summary>
    public bool AppUpdateBannerVisible => General.UpdateBannerVisible && !(IsSettings && IsSettingsGeneral);

    /// <summary>
    /// Whether the reconnect offer belongs in the section footer: the editable sections carry it there instead
    /// of the floating notice.
    /// </summary>
    public bool ReconnectPromptInSection => ShowContent && SettingsSection is "profile" or "config" or "routing";

    /// <summary>
    /// Whether the section footer offers the reconnect that applies the saved settings: it takes the strip once
    /// the Save bar leaves it, and stays away while another edit is open or the offer was put off.
    /// </summary>
    public bool ShowReconnectBar => Home.RestartPending
        && !_reconnectDeferred
        && ReconnectPromptInSection
        && !Config.ShowSaveBar
        && !Routing.ShowSaveBar
        && !Profile.ShowSaveBar;

    /// <summary>
    /// Re-arms the footer reconnect offer, so a save asks again after an earlier one was put off.
    /// </summary>
    public void ArmReconnectPrompt()
    {
        _reconnectDeferred = false;
        OnPropertyChanged(nameof(ShowReconnectBar));
    }

    /// <summary>
    /// Re-gates the footer reconnect offer after the agent's restart flag changes; a fresh requirement re-arms it.
    /// </summary>
    public void NotifyRestartPendingChanged(bool pending)
    {
        if (pending)
        {
            _reconnectDeferred = false;
        }

        OnPropertyChanged(nameof(ShowReconnectBar));
    }

    // Footer offer: put the reconnect off, leaving the settings saved but unapplied.
    [RelayCommand]
    private void DismissReconnect()
    {
        _reconnectDeferred = true;
        OnPropertyChanged(nameof(ShowReconnectBar));
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
        return Config.ConfigNames;
    }

    [RelayCommand]
    private void NavHome()
    {
        Nav = "home";
        Profile.OpenProfile = null;
        Config.AbandonCreate();
        Routing.AbandonCreate();
        Routing.LeaveSection();
        RefreshLogsActive();
    }

    // Back arrow: in a compact section detail step back to the rail, otherwise return home.
    [RelayCommand]
    private void NavBack()
    {
        if (IsCompact && SettingsDetailOpen)
        {
            SettingsDetailOpen = false;
            RefreshLogsActive();
            return;
        }

        NavHome();
    }

    /// <summary>
    /// Opens the settings General section, where the app-update status line lives, so a windowless update that
    /// ended in a failure surfaces its reason instead of a bare home screen (#22).
    /// </summary>
    public void ShowUpdateStatus()
    {
        SettingsSection = "general";
        SettingsDetailOpen = true;
        Nav = "settings";
    }

    /// <summary>
    /// Reopens the view the app was left on before a self-update, from the CurrentView token it handed the
    /// installer. Home is the default, so only a settings token moves anywhere; an unknown section is ignored.
    /// </summary>
    public void RestoreView(string view)
    {
        if (!view.StartsWith("settings", StringComparison.Ordinal))
        {
            return;
        }

        var slash = view.IndexOf('/');
        var section = slash > 0 ? view[(slash + 1)..] : string.Empty;
        if (section is "profile" or "config" or "routing" or "general" or "sources" or "logs")
        {
            SettingsSection = section;
        }

        SettingsDetailOpen = true;
        Nav = "settings";
        SelectSectionDefault(SettingsSection);
        RefreshLogsActive();
    }

    /// <summary>
    /// Открывает конфигурации на секции импорта, для перехода из профилей без конфигураций.
    /// </summary>
    public void ShowConfigImport()
    {
        Nav = "settings";
        SettingsSection = "config";
        SettingsDetailOpen = true;
        Config.EnterImportSection();
    }

    // Home «Добавить профиль»: переход в настройки на секцию профилей.
    [RelayCommand]
    private void AddProfile()
    {
        Nav = "settings";
        SettingsSection = "profile";
        SettingsDetailOpen = true;
        Profile.EnterSection();
        RefreshLogsActive();
    }

    [RelayCommand]
    private void NavSettings()
    {
        // Open the active profile when entering settings so the profile-scoped sections show its config / routing.
        if (Home.ActiveProfile is not null)
        {
            Profile.OpenProfile = Home.ActiveProfile;
        }

        // Compact mode opens on the section rail; the wide layout shows the rail and content together.
        SettingsDetailOpen = false;
        Nav = "settings";

        // Re-entering settings lands on the persisted section without a section-change event, so seed its
        // selection here too.
        SelectSectionDefault(SettingsSection);
        RefreshLogsActive();
    }

    // Fill an empty Routing / Config section with the first available item so it never opens on a blank editor.
    // The active profile's own config / routing list is already reflected by the profile cascade; this is the
    // fallback when that leaves nothing (no active profile, or it assigns none).
    private void SelectSectionDefault(string section)
    {
        if (section == "routing")
        {
            Routing.EnterSection();
        }
        else if (section == "config")
        {
            Config.EnterSection();
        }
        else if (section == "profile")
        {
            Profile.EnterSection();
        }
    }

    [RelayCommand]
    private void SelectSettings(string section)
    {
        SettingsSection = section;
        // Compact mode drills into the section detail; wide mode ignores this and swaps content in place.
        SettingsDetailOpen = true;
        // Reselecting the already-selected section fires no section-change event, so activate here too.
        RefreshLogsActive();
    }

    // The logs viewer's heartbeat re-reads and its initial file listing run only while its content is actually
    // shown: its section is selected in settings, and in compact mode only once drilled into the detail (the
    // rail hides the content). Recompute after every entry that lands there, including those with no
    // section-change event: a restored section on open, or reselecting the already-selected section.
    private void RefreshLogsActive()
    {
        Diagnostics.SetActive(Nav == "settings" && SettingsSection == "logs" && (!IsCompact || SettingsDetailOpen));
    }

    // Импорт брошенных драгом файлов: тип определяется по содержимому, конфиги и списки маршрутизации
    // добавляются в каталог с авто-инкрементом имени, бэкап отклоняется с подсказкой (окно импорта).
    [RelayCommand]
    private async Task ImportDroppedFiles(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return;
        }

        var configTaken = new HashSet<string>(Config.ConfigNames, StringComparer.Ordinal);
        var routingTaken = new HashSet<string>(Routing.ListNames, StringComparer.Ordinal);
        var configs = 0;
        var routing = 0;
        var bundle = 0;
        var rejected = 0;

        foreach (var path in paths)
        {
            var raw = await TryReadFileAsync(path);
            if (raw is null)
            {
                rejected++;
                continue;
            }

            var item = ImportDispatcher.Classify(raw);
            if (item.Kind == DroppedKind.VpnConfig)
            {
                if (await Config.ImportDroppedConfigAsync(item.Config!, configTaken, Path.GetFileNameWithoutExtension(path)))
                {
                    configs++;
                }
                else
                {
                    rejected++;
                }
            }
            else if (item.Kind == DroppedKind.RoutingList)
            {
                if (await Routing.ImportDroppedListAsync(item.RoutingText!, routingTaken))
                {
                    routing++;
                }
                else
                {
                    rejected++;
                }
            }
            else if (item.Kind == DroppedKind.Bundle)
            {
                bundle++;
            }
            else
            {
                rejected++;
            }
        }

        Home.ShowNotice(BuildImportNotice(configs, routing, bundle, rejected));
    }

    private static async Task<byte[]?> TryReadFileAsync(string path)
    {
        try
        {
            return await File.ReadAllBytesAsync(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? BuildImportNotice(int configs, int routing, int bundle, int rejected)
    {
        var parts = new List<string>();
        if (configs > 0)
        {
            parts.Add(Loc.Instance.Get("Drop_ImportedConfigs", configs));
        }

        if (routing > 0)
        {
            parts.Add(Loc.Instance.Get("Drop_ImportedRouting", routing));
        }

        if (bundle > 0)
        {
            parts.Add(Loc.Instance.Get("Drop_BundleHint"));
        }

        if (rejected > 0)
        {
            parts.Add(Loc.Instance.Get("Drop_Skipped", rejected));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    // Persist the selected settings section (#51) whenever it changes.
    partial void OnSettingsSectionChanged(string value)
    {
        _prefs.SettingsSection = value;
        _prefs.Save();
        UpdateActiveSection();

        // Changing section disarms any pending delete confirmation AND clears any blocked-delete reason line in
        // the section being left, so a stale red error does not linger on return (#3/#4).
        Config.ConfigDeletePending = false;
        Profile.ProfileDeletePending = false;
        Routing.RoutingDeletePending = false;
        Config.ConfigDeleteStatus = string.Empty;
        Profile.ProfileDeleteStatus = string.Empty;
        Routing.RoutingDeleteStatus = string.Empty;

        // Leaving config discards an in-progress new-config draft (and stops its scanner).
        if (value != "config")
        {
            Config.AbandonCreate();
        }

        // Leaving routing discards an in-progress import draft (and stops its scanner) and drops the geo entries
        // fetched into the rule list.
        if (value != "routing")
        {
            Routing.AbandonCreate();
            Routing.LeaveSection();
        }

        // Opening the log section loads the on-disk files at once, rather than waiting for the next heartbeat.
        RefreshLogsActive();

        // Landing on a profile-scoped section with nothing open selects the current (active) profile, so the
        // section shows its config / routing instead of an empty editor. Opening the profile cascades into the
        // Config and Routing sections (OnOpenProfileChanged).
        if (value is "profile" or "config" or "routing" && Profile.OpenProfile is null && Home.ActiveProfile is not null)
        {
            Profile.OpenProfile = Home.ActiveProfile;
        }

        // Still empty after the profile cascade: fall back to the first available list / config.
        SelectSectionDefault(value);
    }

    // Push the active-section flag to the config / routing screens so their footer Save bar shows only for
    // the section on screen.
    private void UpdateActiveSection()
    {
        Config.IsActiveSection = SettingsSection == "config";
        Routing.IsActiveSection = SettingsSection == "routing";
        Profile.IsActiveSection = SettingsSection == "profile";
    }

    private void OnGeneralPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GeneralViewModel.UpdateBannerVisible))
        {
            OnPropertyChanged(nameof(AppUpdateBannerVisible));
        }
    }

    // Re-gate the footer reconnect offer when a section's Save bar takes or frees the strip.
    private void OnSectionBarChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigViewModel.ShowSaveBar))
        {
            OnPropertyChanged(nameof(ShowReconnectBar));
        }
    }

    // Push the compact-layout flag to every section screen so their rows restack for the narrow window.
    partial void OnWindowWidthChanged(double value)
    {
        var compact = value < CompactBreakpoint;
        Config.IsCompact = compact;
        Routing.IsCompact = compact;
        Sources.IsCompact = compact;
        Diagnostics.IsCompact = compact;
        General.IsCompact = compact;
        Profile.IsCompact = compact;

        // A width flip can reveal or hide the logs content (compact rail vs wide content), so re-evaluate.
        RefreshLogsActive();
    }

    private void OnConnected()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Home.SetConnected();
            // Re-arm the logs viewer after a reconnect; the disconnect stopped its poll.
            RefreshLogsActive();
        });
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
            Diagnostics.Reset();
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
        Diagnostics.Apply(snapshot);
    }
}
