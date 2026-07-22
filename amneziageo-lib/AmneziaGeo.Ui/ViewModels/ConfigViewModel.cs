using System.Collections.ObjectModel;
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
    private string? _pendingOpenConfig;
    private string? _configBeforeCreate;
    private string _sectionConfigDefaultName = string.Empty;
    private bool _sectionConfigSaving;
    private bool _suppressCatalogueConfig;

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
    private string? _openConfig;

    [ObservableProperty]
    private ExportDialogViewModel? _configExport;

    [ObservableProperty]
    private ConfigTransportViewModel? _configTransport;

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
    [NotifyPropertyChangedFor(nameof(SegImportActive))]
    [NotifyPropertyChangedFor(nameof(SegConfigActive))]
    [NotifyPropertyChangedFor(nameof(SegExportActive))]
    [NotifyPropertyChangedFor(nameof(ShowSaveBar))]
    [NotifyPropertyChangedFor(nameof(ShowSaveButton))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isCreatingSectionConfig;

    // Manage sub-section shown by the top menu (Config vs Export). Import is IsCreatingSectionConfig.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSectionConfig))]
    [NotifyPropertyChangedFor(nameof(IsSectionExport))]
    [NotifyPropertyChangedFor(nameof(SegConfigActive))]
    [NotifyPropertyChangedFor(nameof(SegExportActive))]
    [NotifyPropertyChangedFor(nameof(IsEditDirty))]
    [NotifyPropertyChangedFor(nameof(ShowSaveBar))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
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
    [NotifyPropertyChangedFor(nameof(SectionMethodLabel))]
    [NotifyPropertyChangedFor(nameof(ShowSaveButton))]
    [NotifyPropertyChangedFor(nameof(ShowSaveBar))]
    private ConfigImportMethod _importMethod = ConfigImportMethod.Picker;

    // Live QR scanner for the create form; non-null only while the camera method is active.
    [ObservableProperty]
    private ScanViewModel? _sectionScan;

    /// <summary>
    /// ctor
    /// </summary>
    public ConfigViewModel(MainWindowViewModel host, IAgentConnection connection)
    {
        _host = host;
        _connection = connection;
        Loc.Instance.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged()
    {
        OnPropertyChanged(nameof(DeleteConfigPrompt));
        OnPropertyChanged(nameof(SectionMethodLabel));
    }

    /// <summary>
    /// Configuration rows.
    /// </summary>
    public ObservableCollection<ConfigItemViewModel> Configs { get; } = [];

    public ObservableCollection<ConfigChoice> ConfigCatalogueOptions { get; } = [ConfigChoice.None];

    /// <summary>
    /// The names of the configurations currently known.
    /// </summary>
    public IReadOnlyList<string> ConfigNames => _configNames;

    public bool HasConfigs => Configs.Count > 0;

    public bool IsConfigManage => OpenConfig is not null;

    public bool IsSectionImport => IsCreatingSectionConfig;

    public bool IsSectionConfig => !IsCreatingSectionConfig && OpenConfig is not null && ManageSection == ConfigSection.Config;

    public bool IsSectionExport => !IsCreatingSectionConfig && OpenConfig is not null && ManageSection == ConfigSection.Export;

    // Segment highlight, independent of an open config so the pill still marks the section when none is selected.
    public bool SegImportActive => IsCreatingSectionConfig;

    public bool SegConfigActive => !IsCreatingSectionConfig && ManageSection == ConfigSection.Config;

    public bool SegExportActive => !IsCreatingSectionConfig && ManageSection == ConfigSection.Export;

    public bool CanConfigSection => HasConfigs;

    public bool CanExportSection => HasConfigs;

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

    public string SectionMethodLabel => ImportMethod switch
    {
        ConfigImportMethod.Manual => Loc.Instance.Get("Main_MethodLabel", Loc.Instance.Get("Main_MethodManual")),
        ConfigImportMethod.Camera => Loc.Instance.Get("Main_MethodLabel", Loc.Instance.Get("Main_MethodCamera")),
        _ => string.Empty,
    };

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

    // Landing on the Config section with nothing open: open the active profile's config, or the first one, so it
    // never opens empty. A create-form draft in progress is left alone.
    public void SelectFirstIfNone()
    {
        if (OpenConfig is not null || IsCreatingSectionConfig || Configs.Count == 0)
        {
            return;
        }

        OpenConfig = PreferredDefaultConfig();
    }

    // The active profile's config when it is one of ours, else the first in the catalogue.
    private string PreferredDefaultConfig()
    {
        var active = _host.Home.ActiveProfile;
        if (active is { Config.Length: > 0 } && Configs.Any(c => string.Equals(c.Name, active.Config, StringComparison.Ordinal)))
        {
            return active.Config;
        }

        return Configs[0].Name;
    }

    // Entering the config settings section: keep an in-progress draft, land on the active / first config, or fall
    // back to Import when there are no configs to show.
    public void EnterSection()
    {
        if (IsCreatingSectionConfig)
        {
            return;
        }

        if (Configs.Count == 0)
        {
            BeginSectionConfig();
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
            if (!IsCreatingSectionConfig)
            {
                BeginSectionConfig();
            }

            return;
        }

        LeaveImport();
        SelectFirstIfNone();
        ManageSection = target == "export" ? ConfigSection.Export : ConfigSection.Config;
        if (ManageSection == ConfigSection.Export)
        {
            ConfigExport?.RefreshExport();
        }
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
        NotifyHasConfigsChanged();
    }

    /// <summary>
    /// Tears down all config state on disconnect: the backing rows are about to be cleared, so a stale editor
    /// would keep firing IPC at a dead pipe until the next reconnect snapshot rebuilds the list.
    /// </summary>
    public void Reset()
    {
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

    private void NotifyHasConfigsChanged()
    {
        OnPropertyChanged(nameof(HasConfigs));
        OnPropertyChanged(nameof(CanConfigSection));
        OnPropertyChanged(nameof(CanExportSection));
    }

    partial void OnOpenConfigChanged(string? value)
    {
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
        ConfigTransport = new ConfigTransportViewModel(_connection, value, item?.Endpoint ?? string.Empty, item?.UseWebSocket ?? false, item?.WebSocketHost ?? string.Empty, item?.WebSocketPort ?? 443, item?.Mtu ?? 0);
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

    partial void OnSectionConfigTextChanged(string value) => SectionConfigStatus = string.Empty;

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

    // The config catalogue for a profile's combo. Order: «— не выбрано —» (None) then every config (shared /
    // reusable across profiles). Creating a config is the «+ Новая конфигурация» button in the Config section,
    // not a combo entry (#111).
    public IReadOnlyList<ConfigChoice> BuildConfigOptions()
    {
        var options = new List<ConfigChoice> { ConfigChoice.None };
        foreach (var name in _configNames)
        {
            options.Add(new ConfigChoice(name));
        }

        return options;
    }

    // The first config that is not the one just deleted (still present in the collection until the next
    // snapshot drops it), or null when it was the last one.
    private string? NextConfigAfter(string deleted) =>
        Configs.FirstOrDefault(c => !string.Equals(c.Name, deleted, StringComparison.Ordinal))?.Name;

    internal async Task<IpcAck> ImportConfigAsync(string name, string confText)
    {
        return await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpImportConfig, [name, confText]));
    }

    internal async Task<IpcAck> RemoveConfigAsync(string name)
    {
        return await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpRemoveConfig, [name]));
    }

    // The config Delete trigger (#147): deletion unbinds the config from any profile that references it; the agent
    // refuses only when it is the config of the running profile. Arm the inline confirm/cancel pair (#4).
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

    // Inline Confirm: perform the delete. The agent unbinds the config from referencing profiles and refuses only
    // when it is the running profile's config. On success the next remaining config opens so the section is never left empty.
    [RelayCommand]
    private async Task ConfirmDeleteOpenConfig()
    {
        ConfigDeletePending = false;
        if (OpenConfig is null)
        {
            return;
        }

        var config = OpenConfig;
        var ack = await RemoveConfigAsync(config);
        if (!ack.Ok)
        {
            ConfigDeleteStatus = ack.Message;
            return;
        }

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
        // Pre-fill a unique default name (like a new profile, #117) so the required-name field is never empty
        // on open; the user can accept it or type over it. Clearing it turns the box red and blocks the save.
        // Remember the default so an import can still overwrite it with the config's own name (SectionConfigNameIsDefault).
        _sectionConfigDefaultName = UniqueConfigName();
        SectionConfigName = _sectionConfigDefaultName;
        SectionConfigText = string.Empty;
        SectionConfigStatus = string.Empty;
        ImportMethod = ConfigImportMethod.Picker;
        SectionScan = null;
        IsCreatingSectionConfig = true;
    }

    // Discards the create-form draft. Called when the import section is left (tab switch / home) and on disconnect.
    private void CancelSectionConfig()
    {
        IsCreatingSectionConfig = false;
        SectionConfigName = string.Empty;
        SectionConfigText = string.Empty;
        SectionConfigStatus = string.Empty;
        ImportMethod = ConfigImportMethod.Picker;
        SectionScan = null;
    }

    // Switch the create form to manual entry.
    [RelayCommand]
    private void BeginManualImport()
    {
        ImportMethod = ConfigImportMethod.Manual;
    }

    // Switch the create form to the live QR scanner.
    [RelayCommand]
    private void BeginCameraImport()
    {
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

    // Return to the method picker from manual / camera, discarding the drafted text.
    [RelayCommand]
    private void ChangeMethod()
    {
        SectionConfigText = string.Empty;
        SectionConfigStatus = string.Empty;
        SectionScan = null;
        ImportMethod = ConfigImportMethod.Picker;
    }

    // Footer Save/Cancel: the same bar serves the import draft and the open-config edits.
    [RelayCommand]
    private async Task SaveSection()
    {
        if (IsCreatingSectionConfig)
        {
            await SaveSectionConfig();
        }
        else
        {
            await SaveConfigEdit();
        }
    }

    // Footer Cancel: an import draft returns to the method picker in place (discards the drafted text, no
    // navigation); an open-config edit reverts to its baseline. Leaving the import section fully is the top tabs.
    [RelayCommand]
    private void CancelSection()
    {
        if (IsCreatingSectionConfig)
        {
            ChangeMethod();
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
        SectionConfigText = imported.ConfText;
        if (SectionConfigNameIsDefault && !string.IsNullOrWhiteSpace(imported.Name))
        {
            SectionConfigName = imported.Name!;
        }

        SectionConfigStatus = string.Empty;
        SectionScan = null;
        ImportMethod = ConfigImportMethod.Manual;
    }

    // Stops the live scanner when the create form leaves view (section change / home / hide).
    public void StopScan()
    {
        if (ImportMethod == ConfigImportMethod.Camera)
        {
            SectionScan = null;
            ImportMethod = ConfigImportMethod.Picker;
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
            _host.Profile.AdoptConfig(name);
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

    // A unique default name for a new config, mirroring UniqueProfileName (#117): a new item is pre-named so the
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
