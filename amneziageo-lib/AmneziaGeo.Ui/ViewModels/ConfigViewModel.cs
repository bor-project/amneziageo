using System.Collections.ObjectModel;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Config screen: the shared configuration catalogue, the open-config manage editors (.conf / transport /
/// rename / delete), and the standalone "+ Новая конфигурация" import form. The shared-namespace name check
/// lives on the shell, reached through <c>_host</c>.
/// </summary>
internal sealed partial class ConfigViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _host;
    private readonly IAgentConnection _connection;

    private IReadOnlyList<string> _configNames = [];

    // The order a drag just sent; held until a snapshot arrives carrying it.
    private IReadOnlyList<string>? _pendingOrder;
    private string? _pendingOpenConfig;
    private string? _configBeforeCreate;
    private string _sectionConfigDefaultName = string.Empty;
    private bool _sectionConfigSaving;
    private bool _suppressCatalogueConfig;

    // Whether the agent has reported its config catalogue, and whether entering the section waits for it.
    private bool _catalogueKnown;
    private bool _enterDeferred;

    // The import was started from the home server list, which the saved configuration returns to.
    private bool _returnToServers;

    // Rename baseline: the open config's persisted name; a differing ConfigRename saves on the section Save.
    private string _baseConfigRename = string.Empty;

    // Narrow-window layout flag, pushed by the shell.
    [ObservableProperty]
    private bool _isCompact;

    // Whether this section is the one currently shown, pushed by the shell; gates the footer Save bar so a
    // dirty edit does not bleed the bar over another section.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSaveBar))]
    private bool _isActiveSection;

    [ObservableProperty]
    private ConfigChoice? _selectedCatalogueConfig = ConfigChoice.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfigManage))]
    [NotifyPropertyChangedFor(nameof(IsSectionConfig))]
    [NotifyPropertyChangedFor(nameof(IsSectionExport))]
    [NotifyPropertyChangedFor(nameof(DeleteConfigPrompt))]
    [NotifyPropertyChangedFor(nameof(IsOpenConfigActive))]
    [NotifyPropertyChangedFor(nameof(UseOpenConfig))]
    [NotifyPropertyChangedFor(nameof(ShowConnectOpenConfig))]
    [NotifyPropertyChangedFor(nameof(CanExportOpenConfig))]
    [NotifyPropertyChangedFor(nameof(ShowNoConfigsHint))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderActive))]
    private string? _openConfig;

    [ObservableProperty]
    private ExportDialogViewModel? _configExport;

    [ObservableProperty]
    private ConfigTransportViewModel? _configTransport;

    // Whether the .conf text is on screen. Off until asked for: the text carries the private key.
    [ObservableProperty]
    private bool _showConfigText;

    [ObservableProperty]
    private string _configDeleteStatus = string.Empty;

    [ObservableProperty]
    private bool _configDeletePending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigNameMissing))]
    private string _configRename = string.Empty;

    [ObservableProperty]
    private string _configRenameStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSectionImport))]
    [NotifyPropertyChangedFor(nameof(IsSectionConfig))]
    [NotifyPropertyChangedFor(nameof(IsSectionExport))]
    [NotifyPropertyChangedFor(nameof(ShowSaveBar))]
    [NotifyPropertyChangedFor(nameof(ShowSaveButton))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderActions))]
    [NotifyPropertyChangedFor(nameof(ShowNoConfigsHint))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderActive))]
    private bool _isCreatingSectionConfig;

    // Manage sub-section shown by the top menu (Config vs Export). Import is IsCreatingSectionConfig.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSectionConfig))]
    [NotifyPropertyChangedFor(nameof(IsSectionExport))]
    [NotifyPropertyChangedFor(nameof(IsEditDirty))]
    [NotifyPropertyChangedFor(nameof(ShowSaveBar))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderActions))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderActive))]
    private ConfigSection _manageSection = ConfigSection.Config;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveSectionConfig))]
    [NotifyPropertyChangedFor(nameof(SectionConfigNameMissing))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _sectionConfigName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveSectionConfig))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _sectionConfigText = string.Empty;

    [ObservableProperty]
    private string _sectionConfigStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImportPicker))]
    [NotifyPropertyChangedFor(nameof(IsImportManual))]
    [NotifyPropertyChangedFor(nameof(IsImportCamera))]
    [NotifyPropertyChangedFor(nameof(ShowSaveButton))]
    [NotifyPropertyChangedFor(nameof(ShowSaveBar))]
    private ConfigImportMethod _importMethod = ConfigImportMethod.Picker;

    // Live QR scanner for the create form; non-null only while the camera method is active.
    [ObservableProperty]
    private ScanViewModel? _sectionScan;

    // Transport of the config being added: the same proxy / MTU / IPv6 the open config carries, committed
    // once the import gave the configuration a name.
    [ObservableProperty]
    private ConfigTransportViewModel? _sectionTransport;

    // Каталог стоит списком слева, а его настройки справа: выбор и «Добавить» из шапки секции уходят туда.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCataloguePicker))]
    [NotifyPropertyChangedFor(nameof(ShowAddAction))]
    private bool _isWide;

    // Строка поиска списка слева: сужает каталог по имени и адресу.
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// ctor
    /// </summary>
    public ConfigViewModel(MainWindowViewModel host, IAgentConnection connection)
    {
        _host = host;
        _connection = connection;
        Configs.CollectionChanged += (_, _) => RebuildVisibleConfigs();
        Loc.Instance.CultureChanged += OnCultureChanged;
    }

    partial void OnSearchTextChanged(string value)
    {
        RebuildVisibleConfigs();
    }

    // Пересобирает список под поиском.
    private void RebuildVisibleConfigs()
    {
        var query = SearchText.Trim();
        VisibleConfigs.Clear();
        foreach (var item in Configs)
        {
            if (query.Length == 0
                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Endpoint.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                VisibleConfigs.Add(item);
            }
        }
    }

    private void OnCultureChanged()
    {
        OnPropertyChanged(nameof(DeleteConfigPrompt));
    }

    /// <summary>
    /// Набор способов оболочки: шторка «Добавить» / «Экспорт».
    /// </summary>
    public ActionSheetViewModel Sheet => _host.Sheet;

    /// <summary>
    /// Configuration rows.
    /// </summary>
    public ObservableCollection<ConfigItemViewModel> Configs { get; } = [];

    /// <summary>
    /// Каталог под строкой поиска: список широкой раскладки берёт его, узкая - весь каталог.
    /// </summary>
    public ObservableCollection<ConfigItemViewModel> VisibleConfigs { get; } = [];

    public ObservableCollection<ConfigChoice> ConfigCatalogueOptions { get; } = [];

    /// <summary>
    /// The same catalogue for the home picker, «не выбрано» first: the connection card takes no configuration
    /// at all, while the settings picker always shows one.
    /// </summary>
    public ObservableCollection<ConfigChoice> HomeConfigOptions { get; } = [ConfigChoice.None];

    /// <summary>
    /// The names of the configurations currently known.
    /// </summary>
    public IReadOnlyList<string> ConfigNames => _configNames;

    public bool HasConfigs => Configs.Count > 0;

    /// <summary>
    /// Whether the section header carries the catalogue picker: the wide layout has the list beside it.
    /// </summary>
    public bool ShowCataloguePicker => HasConfigs && !IsWide;

    /// <summary>
    /// Whether the section header carries «Добавить»: the wide layout stands it over the list.
    /// </summary>
    public bool ShowAddAction => !IsWide;

    public bool IsConfigManage => OpenConfig is not null;

    public bool IsSectionImport => IsCreatingSectionConfig;

    public bool IsSectionConfig => !IsCreatingSectionConfig && OpenConfig is not null && ManageSection == ConfigSection.Config;

    public bool IsSectionExport => !IsCreatingSectionConfig && OpenConfig is not null && ManageSection == ConfigSection.Export;

    /// <summary>
    /// Стоит ли тумблер активности в шапке: отдельная карточка под выбором стоила пол-экрана, и её содержимое
    /// ужато в строку у самого выбора.
    /// </summary>
    public bool ShowHeaderActive => IsSectionConfig;

    /// <summary>
    /// Стоит ли на экране выбор конфигурации с кнопками «Добавить» и «Экспорт»: черновик, сканер и экспорт
    /// занимают экран целиком и возвращают сюда сами.
    /// </summary>
    public bool ShowHeaderActions => !IsCreatingSectionConfig && !IsSectionExport && !ShowCatalogueLoader;

    /// <summary>
    /// Есть ли что экспортировать.
    /// </summary>
    public bool CanExportOpenConfig => OpenConfig is not null;

    /// <summary>
    /// Пуст ли каталог: подсказка вместо настроек, пока конфигурации не добавлены.
    /// </summary>
    public bool ShowNoConfigsHint => !ShowCatalogueLoader && !IsCreatingSectionConfig && OpenConfig is null;

    /// <summary>
    /// Delete-card prompt naming the open config.
    /// </summary>
    public string DeleteConfigPrompt => Loc.Instance.Get("Main_DeleteConfigPrompt", OpenConfig ?? string.Empty);

    public bool ConfigNameMissing => string.IsNullOrWhiteSpace(ConfigRename);

    public bool SectionConfigNameMissing => string.IsNullOrWhiteSpace(SectionConfigName);

    public bool SectionConfigNameIsDefault =>
        string.IsNullOrWhiteSpace(SectionConfigName)
        || string.Equals(SectionConfigName, _sectionConfigDefaultName, StringComparison.Ordinal);

    public bool CanSaveSectionConfig =>
        VpnLinkCodec.TryDecode(SectionConfigText) is not null && !string.IsNullOrWhiteSpace(SectionConfigName);

    public bool IsImportPicker => ImportMethod == ConfigImportMethod.Picker;

    public bool IsImportManual => ImportMethod == ConfigImportMethod.Manual;

    public bool IsImportCamera => ImportMethod == ConfigImportMethod.Camera;

    /// <summary>
    /// Whether a live camera QR scanner is available on this platform.
    /// </summary>
    public bool CameraScanAvailable => QrCameraScannerHost.IsAvailable;

    // ---- Section Save/Cancel bar (#143): the open-config edits (name / .conf text / transport) are held and
    // committed atomically on the footer Save, reverted on Cancel; the same footer serves the import draft. ----

    // The open config's edits differ from their loaded baseline (name / .conf / transport).
    private bool RenameDirty => !string.Equals(ConfigRename ?? string.Empty, _baseConfigRename, StringComparison.Ordinal);

    /// <summary>
    /// Whether the open-config editors hold uncommitted changes. Not gated on the sub-section, so the footer
    /// stays up when switching Config ↔ Export with a pending edit (the editors are null during Import).
    /// </summary>
    public bool IsEditDirty =>
        (ConfigExport?.IsDirty ?? false) || (ConfigTransport?.IsDirty ?? false) || RenameDirty;

    /// <summary>
    /// Whether the footer Save/Cancel bar is shown: an import draft, or dirty open-config edits (only while this
    /// section is the one on screen).
    /// </summary>
    public bool ShowSaveBar => IsActiveSection && (IsCreatingSectionConfig ? !IsImportPicker : IsEditDirty);

    /// <summary>
    /// Whether the footer Save button is shown: the import draft shows it once in manual entry; edits always.
    /// </summary>
    public bool ShowSaveButton => !IsCreatingSectionConfig || IsImportManual;

    /// <summary>
    /// Whether the footer Save button is enabled.
    /// </summary>
    public bool CanSave => IsCreatingSectionConfig ? CanSaveSectionConfig : IsEditDirty;

    private void RefreshEditBar()
    {
        OnPropertyChanged(nameof(IsEditDirty));
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(ShowSaveButton));
        OnPropertyChanged(nameof(CanSave));
    }

    private void OnEditScopeDirty(object? sender, EventArgs e) => RefreshEditBar();

    // Landing on the Config section with nothing open: open the active config, or the first one, so it
    // never opens empty. A create-form draft in progress is left alone.
    public void SelectFirstIfNone()
    {
        if (OpenConfig is not null || IsCreatingSectionConfig || Configs.Count == 0)
        {
            return;
        }

        OpenConfig = PreferredDefaultConfig();
    }

    // The active config when it is one of ours, else the first in the catalogue.
    private string PreferredDefaultConfig()
    {
        var active = _host.Home.ActiveConfig;
        if (active is not null && Configs.Any(c => string.Equals(c.Name, active.Name, StringComparison.Ordinal)))
        {
            return active.Name;
        }

        return Configs[0].Name;
    }

    // Re-raise the host-derived flags after the shell recomputes them on a snapshot.
    public void NotifyHostFlagsChanged()
    {
        NotifyActiveConfigChanged();
    }

    // Re-read the active switch after the connection card's active config changes.
    public void NotifyActiveConfigChanged()
    {
        OnPropertyChanged(nameof(IsOpenConfigActive));
        OnPropertyChanged(nameof(UseOpenConfig));
        OnPropertyChanged(nameof(ShowConnectOpenConfig));
    }

    /// <summary>
    /// Whether the open config is the one the next connect binds to.
    /// </summary>
    public bool IsOpenConfigActive =>
        OpenConfig is { Length: > 0 } && string.Equals(OpenConfig, _host.Home.ActiveConfig?.Name, StringComparison.Ordinal);

    /// <summary>
    /// Whether the active card offers to dial the open config: it is the one the next connect binds to, the agent
    /// is there, and no tunnel runs yet.
    /// </summary>
    public bool ShowConnectOpenConfig => IsOpenConfigActive && _host.Home.IsConnected && !_host.Home.IsTunnelActive;

    /// <summary>
    /// Whether the open config is the one the next connect binds to. Turning it on makes the open config active,
    /// turning it off leaves none selected and takes down a tunnel bound to it.
    /// </summary>
    public bool UseOpenConfig
    {
        get => IsOpenConfigActive;
        set
        {
            if (value == IsOpenConfigActive)
            {
                OnPropertyChanged();
                return;
            }

            if (value)
            {
                SetActiveConfig();
                return;
            }

            _ = _host.Home.ClearActiveConfigAsync();
        }
    }

    // Makes the open config the active one (the agent's target), without connecting.
    private void SetActiveConfig()
    {
        if (OpenConfig is not { Length: > 0 } name)
        {
            return;
        }

        _host.Home.ActiveConfig = Configs.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Поднимает туннель на открытой конфигурации.
    /// </summary>
    [RelayCommand]
    private async Task ConnectOpenConfig()
    {
        if (OpenConfig is not { Length: > 0 } name)
        {
            return;
        }

        await _host.Home.ToggleConfigConnectionAsync(name, true);
    }

    /// <summary>
    /// Показывает или прячет текст конфигурации.
    /// </summary>
    [RelayCommand]
    private void ToggleConfigText() => ShowConfigText = !ShowConfigText;

    // Entering the config settings section: keep an in-progress draft, land on the active / first config, or stand
    // on the empty catalogue, where «Добавить» offers the way in.
    public void EnterSection()
    {
        ShowConfigText = false;
        if (IsCreatingSectionConfig)
        {
            return;
        }

        if (Configs.Count == 0)
        {
            // The agent has not reported its configs yet: hold the section on a loader. Opening the import draft
            // here would stick, since a later catalogue never pulls the section back out of it (#137).
            if (!_catalogueKnown)
            {
                _enterDeferred = true;
                OnPropertyChanged(nameof(ShowCatalogueLoader));
                OnPropertyChanged(nameof(ShowHeaderActions));
                OnPropertyChanged(nameof(ShowNoConfigsHint));
            }

            return;
        }

        SelectFirstIfNone();
    }

    // Top menu: Config / Import / Export. Import begins a fresh create draft; Config / Export land on the open
    // config (or the active / first when none is open) and pick the sub-section.
    [RelayCommand]
    private void SelectConfigSection(string target)
    {
        if (target == "import")
        {
            BeginManualImport();
            return;
        }

        ShowConfigText = false;
        LeaveImport();
        SelectFirstIfNone();
        ManageSection = target == "export" ? ConfigSection.Export : ConfigSection.Config;
        if (ManageSection == ConfigSection.Export)
        {
            ConfigExport?.RefreshExport();
        }
    }

    /// <summary>
    /// Возвращает с экрана экспорта и из черновика импорта туда, откуда их открыли. Отдаёт, сделан ли шаг.
    /// </summary>
    public bool TryNavigateBack()
    {
        if (IsSectionExport)
        {
            SelectConfigSection("config");
            return true;
        }

        if (!IsCreatingSectionConfig)
        {
            return false;
        }

        // Черновик снимает возврат к списку серверов, так что цель запоминается до отмены.
        var toServers = _returnToServers;
        AbandonCreate();
        if (toServers)
        {
            _host.ReturnToServerList();
        }

        return true;
    }

    /// <summary>
    /// Переводит секцию конфигураций в импорт. Начатый из списка серверов импорт возвращает к нему после
    /// сохранения.
    /// </summary>
    public void EnterImportSection(bool returnToServers = false)
    {
        _returnToServers = returnToServers;
        if (!IsCreatingSectionConfig)
        {
            BeginSectionConfig();
        }
    }

    /// <summary>
    /// Открывает настройки названной конфигурации, минуя выбор в каталоге.
    /// </summary>
    public void OpenConfigFor(string name)
    {
        if (IsCreatingSectionConfig)
        {
            CancelSectionConfig();
        }

        ManageSection = ConfigSection.Config;
        OpenConfig = name;
    }

    // Discard an in-progress draft before switching to Config / Export.
    private void LeaveImport()
    {
        if (IsCreatingSectionConfig)
        {
            AbandonCreate();
        }
    }

    /// <summary>
    /// Reconciles the config catalogue from the snapshot.
    /// </summary>
    public void Apply(IReadOnlyList<ConfigEntry> entries)
    {
        // Reconcile in place (match by name) rather than Clear()+Add(): rebuilding the collection on
        // every snapshot push would regenerate every row's controls, flickering the list each tick even
        // though usually only the status field moves during a connect. Update the existing rows instead.
        // A drag just sent its order: leave the rows where the user put them until the agent's snapshot
        // carries that order back, so a snapshot already on its way does not throw them about meanwhile.
        var holdOrder = _pendingOrder is { } pending
            && !pending.SequenceEqual(entries.Select(e => e.Name), StringComparer.Ordinal);
        if (!holdOrder)
        {
            _pendingOrder = null;
        }

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
                if (from != i && !holdOrder)
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
            existing.UseIpv6 = entry.UseIpv6;
            existing.HandshakeAgeSeconds = entry.HandshakeAgeSeconds;
            existing.RxBitsPerSecond = entry.RxBitsPerSecond;
            existing.TxBitsPerSecond = entry.TxBitsPerSecond;
            existing.HandshakesPerMinute = entry.HandshakesPerMinute;
            existing.LinkLossPercent = entry.LossPercent;
            existing.LinkRttMs = entry.RttMs;
        }

        _configNames = [.. entries.Select(e => e.Name)];
        ReconcileConfigCatalogueOptions();

        // A config just imported in the Config section: open it once its row arrives so OnOpenConfigChanged
        // seeds the transport editor from the real snapshot row instead of all-defaults.
        ResolvePendingOpenConfig();

        // The open config is gone (deleted from the server list or elsewhere): move to what is left, so the
        // editors never key by a name the agent no longer has.
        if (OpenConfig is { } open && _pendingOpenConfig is null && !_configNames.Contains(open, StringComparer.Ordinal))
        {
            OpenConfig = Configs.FirstOrDefault()?.Name;
        }

        // Re-select the section combo now that the option list is current: an OpenConfig set above (or a
        // pending one just resolved) whose real choice only now exists needs the selection re-pointed at it.
        SyncCatalogueConfig();
        NotifyHasConfigsChanged();
        MarkCatalogueKnown();
    }

    /// <summary>
    /// Tears down all config state on disconnect: the backing rows are about to be cleared, so a stale editor
    /// would keep firing IPC at a dead pipe until the next reconnect snapshot rebuilds the list.
    /// </summary>
    public void Reset()
    {
        _catalogueKnown = false;
        _enterDeferred = false;
        OnPropertyChanged(nameof(ShowCatalogueLoader));
        OnPropertyChanged(nameof(ShowHeaderActions));
        OnPropertyChanged(nameof(ShowNoConfigsHint));
        Configs.Clear();
        OpenConfig = null;
        _pendingOpenConfig = null;
        _configNames = [];
        IsCreatingSectionConfig = false;
        ImportMethod = ConfigImportMethod.Picker;
        SectionScan = null;
        ManageSection = ConfigSection.Config;
        ReconcileConfigCatalogueOptions();
        SyncCatalogueConfig();
        NotifyHasConfigsChanged();
    }

    // Opens the config an import pinned, as soon as the catalogue holds it. Called after the import too: the
    // snapshot carrying the new row can land while the import is still in flight, and waiting for the next push
    // would leave the section on an empty picker.
    private void ResolvePendingOpenConfig()
    {
        if (_pendingOpenConfig is not { } pending || !_configNames.Contains(pending, StringComparer.Ordinal))
        {
            return;
        }

        _pendingOpenConfig = null;
        OpenConfig = pending;
    }

    /// <summary>
    /// Whether the section waits for a catalogue the agent has not reported yet.
    /// </summary>
    public bool ShowCatalogueLoader => _enterDeferred;

    // The first snapshot settles the catalogue: an empty one now means there are no configs, and a section held
    // on its loader can finally land somewhere.
    private void MarkCatalogueKnown()
    {
        if (_catalogueKnown)
        {
            return;
        }

        _catalogueKnown = true;
        if (!_enterDeferred)
        {
            return;
        }

        _enterDeferred = false;
        OnPropertyChanged(nameof(ShowCatalogueLoader));
        OnPropertyChanged(nameof(ShowHeaderActions));
        OnPropertyChanged(nameof(ShowNoConfigsHint));
        if (IsActiveSection)
        {
            EnterSection();
        }
    }

    private void NotifyHasConfigsChanged()
    {
        OnPropertyChanged(nameof(HasConfigs));
        OnPropertyChanged(nameof(ShowCataloguePicker));
    }

    partial void OnOpenConfigChanged(string? value)
    {
        ShowConfigText = false;
        ConfigDeleteStatus = string.Empty;
        ConfigDeletePending = false;
        // Set the rename baseline before the field so seeding it does not read as a dirty edit (#143).
        _baseConfigRename = value ?? string.Empty;
        ConfigRename = value ?? string.Empty;
        ConfigRenameStatus = string.Empty;
        SyncCatalogueConfig();
        if (value is null)
        {
            ConfigExport = null;
            ConfigTransport = null;
            RefreshEditBar();
            return;
        }

        var export = new ExportDialogViewModel(_connection, value);
        ConfigExport = export;
        _ = export.LoadAsync();

        var item = Configs.FirstOrDefault(c => string.Equals(c.Name, value, StringComparison.Ordinal));
        ConfigTransport = new ConfigTransportViewModel(_connection, value, item?.Endpoint ?? string.Empty, item?.UseWebSocket ?? false, item?.WebSocketHost ?? string.Empty, item?.WebSocketPort ?? 443, item?.Mtu ?? 0, item?.UseIpv6 ?? false);
        RefreshEditBar();
    }

    // Subscribe the open-config editors' dirty signal so the footer Save/Cancel bar tracks their state.
    partial void OnConfigExportChanged(ExportDialogViewModel? oldValue, ExportDialogViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.DirtyChanged -= OnEditScopeDirty;
        }

        if (newValue is not null)
        {
            newValue.DirtyChanged += OnEditScopeDirty;
        }

        RefreshEditBar();
    }

    partial void OnConfigTransportChanged(ConfigTransportViewModel? oldValue, ConfigTransportViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.DirtyChanged -= OnEditScopeDirty;
        }

        if (newValue is not null)
        {
            newValue.DirtyChanged += OnEditScopeDirty;
        }

        RefreshEditBar();
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
        RefreshEditBar();
    }

    // The rename field changed: clear a stale validation line (#3) and refresh the Save bar.
    partial void OnConfigRenameChanged(string value)
    {
        ConfigRenameStatus = string.Empty;
        RefreshEditBar();
    }

    // Editing the new-config name or text clears a stale validation / status line (#3).
    partial void OnSectionConfigNameChanged(string value) => SectionConfigStatus = string.Empty;

    partial void OnSectionConfigTextChanged(string value)
    {
        SectionConfigStatus = string.Empty;
        if (VpnLinkCodec.TryDecode(value) is { } imported)
        {
            SeedSectionNameFromConfig(imported);
            SectionTransport?.SeedEndpoint(VpnLinkCodec.HostName(imported.ConfText) ?? string.Empty);
        }
    }

    // Reflect the open config into the section combo without echoing the pick back: a selected real config
    // shows its row, otherwise «— не выбрано —».
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
        ReconcileOptions(ConfigCatalogueOptions, 0);
        ReconcileOptions(HomeConfigOptions, 1);
    }

    // Mirrors the config names onto an option list, leaving the synthetic rows before head untouched.
    private void ReconcileOptions(ObservableCollection<ConfigChoice> options, int head)
    {
        var present = _configNames.ToHashSet(StringComparer.Ordinal);
        for (var i = options.Count - 1; i >= head; i--)
        {
            if (!present.Contains(options[i].Name))
            {
                options.RemoveAt(i);
            }
        }

        for (var i = 0; i < _configNames.Count; i++)
        {
            var name = _configNames[i];
            var slot = head + i;
            var existing = options.Skip(head).FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));
            if (existing is null)
            {
                options.Insert(Math.Min(slot, options.Count), new ConfigChoice(name));
                continue;
            }

            var index = options.IndexOf(existing);
            if (index != slot)
            {
                options.Move(index, slot);
            }
        }
    }

    // The first config that is not the one just deleted (still present in the collection until the next
    // snapshot drops it), or null when it was the last one.
    private string? NextConfigAfter(string deleted) =>
        Configs.FirstOrDefault(c => !string.Equals(c.Name, deleted, StringComparison.Ordinal))?.Name;

    internal async Task<IpcAck> ImportConfigAsync(string name, string confText)
    {
        return await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpImportConfig, [name, confText]));
    }

    /// <summary>
    /// Импортирует брошенный драгом конфиг: уникализирует имя, добавляет в каталог и открывает.
    /// </summary>
    public async Task<bool> ImportDroppedConfigAsync(VpnLinkCodec.Imported imported, ISet<string> reserved, string? fileName = null)
    {
        // Name rule mirrors the file picker: the config's own name, else the dropped file name, else the default.
        var name = !string.IsNullOrWhiteSpace(imported.Name)
            ? UniqueName.Resolve(imported.Name!, reserved)
            : !string.IsNullOrWhiteSpace(fileName)
                ? UniqueName.Resolve(fileName!, reserved)
                : DefaultConfigName(imported.ConfText, reserved);

        var ack = await ImportConfigAsync(name, imported.ConfText);
        if (!ack.Ok)
        {
            return false;
        }

        reserved.Add(name);
        if (!IsCreatingSectionConfig && !IsEditDirty)
        {
            _pendingOpenConfig = name;
            ResolvePendingOpenConfig();
        }

        return true;
    }

    /// <summary>
    /// Stores the order a drag left the server list in.
    /// </summary>
    [RelayCommand]
    private async Task ApplyOrder()
    {
        var names = Configs.Select(config => config.Name).ToList();
        _pendingOrder = names;
        var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpReorderConfigs, names));
        if (!ack.Ok)
        {
            _pendingOrder = null;
            _host.Home.ShowNotice(IpcMessage.TryParse(ack.Message, out var key, out var args)
                ? Loc.Instance.Get(key, args)
                : ack.Message);
        }
    }

    internal async Task<IpcAck> RemoveConfigAsync(string name)
    {
        return await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpRemoveConfig, [name]));
    }

    /// <summary>
    /// Removes a configuration the user may be sitting on: the selection moves to the first one left and a
    /// tunnel bound to it goes down, so the active one deletes like any other. A refused disconnect still runs
    /// the removal, to answer with the agent's own reason.
    /// </summary>
    internal async Task<IpcAck> DeleteConfigAsync(string name)
    {
        if (await _host.Home.EnsureDisconnectedAsync(name))
        {
            await _host.Home.MoveSelectionOffAsync(name);
        }

        return await RemoveConfigAsync(name);
    }

    // The config Delete trigger (#147): the agent refuses while the config is the running target; the refusal
    // reason surfaces on confirm. Arm the inline confirm/cancel pair.
    [RelayCommand]
    private void RequestDeleteOpenConfig()
    {
        if (OpenConfig is null)
        {
            return;
        }

        ConfigDeleteStatus = string.Empty;
        ConfigDeletePending = true;
    }

    // Inline Cancel: disarm the config delete confirm.
    [RelayCommand]
    private void CancelDeleteConfig()
    {
        ConfigDeletePending = false;
        ConfigDeleteStatus = string.Empty;
    }

    // Inline Confirm: perform the delete. The selection and a tunnel bound to the config are taken off it first,
    // and a refusal surfaces its reason. On success the next remaining config opens so the section is never left empty.
    [RelayCommand]
    private async Task ConfirmDeleteOpenConfig()
    {
        ConfigDeletePending = false;
        if (OpenConfig is null)
        {
            return;
        }

        var config = OpenConfig;
        var ack = await DeleteConfigAsync(config);
        if (!ack.Ok)
        {
            ConfigDeleteStatus = ack.Message;
            return;
        }

        // Последний конфиг удалён - секция остаётся на пустом каталоге с кнопкой «Добавить».
        OpenConfig = NextConfigAfter(config);
    }

    // Commit the open config's rename through the agent. Keyed by the current name; on OK it
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
            _baseConfigRename = next;
            _pendingOpenConfig = next;
            return true;
        }

        ConfigRenameStatus = ack.Message;
        return false;
    }

    // Config settings section: standalone "+ Новая конфигурация" import (file / QR-scan / manual, all inline).

    [RelayCommand]
    private void BeginSectionConfig()
    {
        // Remember the open config so Cancel restores it (or «— не выбрано —»). Close it so only the create form shows.
        _configBeforeCreate = OpenConfig;
        OpenConfig = null;
        // Drop any just-saved pending open so a re-opened form does not resolve it into the manage panel.
        _pendingOpenConfig = null;
        // Pre-fill a unique default name (#117) so the required-name field is never empty
        // on open; the user can accept it or type over it. Clearing it turns the box red and blocks the save.
        // Remember the default so an import can still overwrite it with the config's own name (SectionConfigNameIsDefault).
        _sectionConfigDefaultName = UniqueConfigName();
        SectionConfigName = _sectionConfigDefaultName;
        SectionConfigText = string.Empty;
        SectionConfigStatus = string.Empty;
        ImportMethod = ConfigImportMethod.Picker;
        SectionScan = null;
        SectionTransport = NewSectionTransport();
        IsCreatingSectionConfig = true;
    }

    // Transport editor of a config that does not exist yet: defaults, no endpoint until the text is read.
    private ConfigTransportViewModel NewSectionTransport() =>
        new(_connection, string.Empty, string.Empty, false, string.Empty, 443, 0, false);

    // Discards the create-form draft. Called when the import section is left (tab switch / home) and on disconnect.
    private void CancelSectionConfig()
    {
        IsCreatingSectionConfig = false;
        SectionConfigName = string.Empty;
        SectionConfigText = string.Empty;
        SectionConfigStatus = string.Empty;
        ImportMethod = ConfigImportMethod.Picker;
        SectionScan = null;
        SectionTransport = null;
        _returnToServers = false;
    }

    // Открывает черновик, если он ещё не начат: способ выбирается в шторке «Добавить», а не вкладкой.
    private void EnsureSectionConfig()
    {
        if (!IsCreatingSectionConfig)
        {
            BeginSectionConfig();
        }
    }

    /// <summary>
    /// Открывает черновик под разобранный файл или снимок.
    /// </summary>
    public void BeginImportDraft()
    {
        EnsureSectionConfig();
        ImportMethod = ConfigImportMethod.Manual;
    }

    // Способ «Создать вручную».
    [RelayCommand]
    private void BeginManualImport()
    {
        BeginImportDraft();
    }

    // Способ «Сканировать QR-код».
    [RelayCommand]
    private void BeginCameraImport()
    {
        if (!QrCameraScannerHost.IsAvailable)
        {
            return;
        }

        EnsureSectionConfig();
        SectionScan = new ScanViewModel(TryAcceptScannedConfig);
        ImportMethod = ConfigImportMethod.Camera;
    }

    // The scanner reports a decoded QR's raw text; accept it only when it decodes to a config.
    private bool TryAcceptScannedConfig(string text)
    {
        var imported = VpnLinkCodec.TryDecodeQr(text);
        if (imported is null)
        {
            return false;
        }

        ApplyScannedConfig(imported);
        return true;
    }

    // Footer Save/Cancel: the same bar serves the import draft and the open-config edits.
    [RelayCommand]
    private async Task SaveSection()
    {
        _host.ArmReconnectPrompt();
        if (IsCreatingSectionConfig)
        {
            await SaveSectionConfig();
        }
        else
        {
            await SaveConfigEdit();
        }
    }

    // Footer Cancel: an import draft is dropped whole and the section returns to the open configuration; an
    // open-config edit reverts to its baseline.
    [RelayCommand]
    private void CancelSection()
    {
        if (IsCreatingSectionConfig)
        {
            AbandonCreate();
        }
        else
        {
            CancelConfigEdit();
        }
    }

    // Footer Save (open config): commit the dirty .conf text, transport, and rename atomically. A rejected step
    // surfaces its own reason and leaves the rest pending. Order: rename last so the .conf / transport ops still
    // key by the old name.
    private async Task SaveConfigEdit()
    {
        if (OpenConfig is null)
        {
            return;
        }

        if (ConfigExport is { IsDirty: true } export && !await export.CommitAsync())
        {
            RefreshEditBar();
            return;
        }

        if (ConfigTransport is { IsDirty: true } transport)
        {
            if (!await transport.CommitAsync())
            {
                RefreshEditBar();
                return;
            }

            transport.CaptureBaseline();
        }

        if (RenameDirty && !await CommitConfigRenameAsync())
        {
            RefreshEditBar();
            return;
        }

        RefreshEditBar();
    }

    // Footer Cancel (open config): revert the .conf text, transport, and rename to their loaded baseline.
    private void CancelConfigEdit()
    {
        ConfigExport?.Revert();
        ConfigTransport?.Revert();
        ConfigRename = _baseConfigRename;
        ConfigRenameStatus = string.Empty;
        RefreshEditBar();
    }

    // Discard the create draft when the config section is left for another one.
    public void AbandonCreate()
    {
        if (IsCreatingSectionConfig)
        {
            CancelSectionConfig();
            OpenConfig = _configBeforeCreate;
        }
    }

    // Fill the create form from a scanned QR and show it for review.
    private void ApplyScannedConfig(VpnLinkCodec.Imported imported)
    {
        SeedSectionNameFromConfig(imported);
        SectionConfigText = imported.ConfText;
        SectionConfigStatus = string.Empty;
        SectionScan = null;
        ImportMethod = ConfigImportMethod.Manual;
    }

    // Stops the live scanner when the create form leaves view (section change / home / hide). The draft falls back
    // to manual entry: a method-less draft shows a name box over nothing.
    public void StopScan()
    {
        if (ImportMethod == ConfigImportMethod.Camera)
        {
            SectionScan = null;
            ImportMethod = ConfigImportMethod.Manual;
        }
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

        var name = !SectionConfigNameIsDefault
            ? SectionConfigName.Trim()
            : (!string.IsNullOrWhiteSpace(imported.Name) ? UniqueName.ResolveParen(imported.Name!.Trim(), _configNames) : DefaultConfigName(imported.ConfText, _configNames));

        // A taken name is refused, never overwritten: the configuration behind it holds its own keys.
        if (_configNames.Contains(name, StringComparer.Ordinal))
        {
            SectionConfigStatus = Loc.Instance.Get("MainVm_ConfigNameTaken", name);
            return false;
        }

        // The transport is checked before the import, so a bad port / MTU is reported while the form still
        // holds everything the user typed.
        if (SectionTransport is { } draft && !draft.CanCommit())
        {
            return false;
        }

        var firstOfAll = _configNames.Count == 0;
        _sectionConfigSaving = true;
        try
        {
            var ack = await ImportConfigAsync(name, imported.ConfText);
            if (!ack.Ok)
            {
                SectionConfigStatus = ack.Message;
                return false;
            }

            // Первая конфигурация каталога сразу становится выбранной - выбирать больше не из чего.
            if (firstOfAll)
            {
                await _host.Home.AdoptFirstConfigAsync(name);
            }

            // The drafted proxy / MTU / IPv6 now have a configuration to sit on.
            if (SectionTransport is { IsDirty: true } transport)
            {
                transport.Retarget(name);
                await transport.CommitAsync();
            }

            IsCreatingSectionConfig = false;
            SectionConfigName = string.Empty;
            SectionConfigText = string.Empty;
            SectionConfigStatus = string.Empty;
            SectionTransport = null;
            // Open the just-imported config once its row lands in the next snapshot, so the transport editor
            // seeds from the real config row rather than all-defaults (the row is not in Configs yet here).
            _pendingOpenConfig = name;
            ResolvePendingOpenConfig();

            // Started from the home server list: hand the screen back to it.
            if (_returnToServers)
            {
                _returnToServers = false;
                _host.ReturnToServerList();
            }

            return true;
        }
        finally
        {
            _sectionConfigSaving = false;
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

    // A unique default name for a new config (#117): a new item is pre-named so the
    // required-name field is never empty on open; "<base>", then "<base> 2", "<base> 3"…
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

    /// <summary>
    /// Заполняет имя создаваемого конфига из распознанного конфига, пока в поле стоит дефолт: своё имя, иначе
    /// имя файла, иначе хост без порта (дедуп " (N)").
    /// </summary>
    public void SeedSectionNameFromConfig(VpnLinkCodec.Imported imported, string? fileName = null)
    {
        if (!SectionConfigNameIsDefault)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(imported.Name))
        {
            SectionConfigName = UniqueName.ResolveParen(imported.Name!.Trim(), _configNames);
            return;
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            SectionConfigName = UniqueName.ResolveParen(fileName!.Trim(), _configNames);
            return;
        }

        var host = VpnLinkCodec.HostName(imported.ConfText);
        if (!string.IsNullOrWhiteSpace(host))
        {
            SectionConfigName = UniqueName.ResolveParen(host!, _configNames);
        }
    }

    // Имя по умолчанию для конфига без своего имени и имени файла: хост без порта, иначе локализованный дефолт.
    private string DefaultConfigName(string confText, IEnumerable<string> taken)
    {
        var host = VpnLinkCodec.HostName(confText);
        var baseName = !string.IsNullOrWhiteSpace(host) ? host! : Loc.Instance.Get("MainVm_NewConfigDefaultName");
        return UniqueName.ResolveParen(baseName, taken);
    }
}

/// <summary>
/// Config create-form import method.
/// </summary>
internal enum ConfigImportMethod
{
    Picker,
    Manual,
    Camera,
}

/// <summary>
/// Config screen manage sub-section.
/// </summary>
internal enum ConfigSection
{
    Config,
    Export,
}
