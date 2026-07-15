using System.Collections.ObjectModel;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Config screen: the shared configuration catalogue, the open-config manage editors (.conf / transport /
/// rename / delete), and the standalone "+ Новая конфигурация" import form. The shared-namespace name check
/// lives on the shell, reached through <c>_host</c>.
/// </summary>
internal sealed partial class ConfigViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _host;
    private readonly AgentConnection _connection;

    private IReadOnlyList<string> _configNames = [];
    private string? _pendingOpenConfig;
    private string? _configBeforeCreate;
    private bool _autoSaving;
    private bool _autoSavePending;
    private string _sectionConfigDefaultName = string.Empty;
    private bool _sectionConfigSaving;
    private bool _suppressCatalogueConfig;

    // Rename baseline: the open config's persisted name; a differing ConfigRename autosaves on blur.
    private string _baseConfigRename = string.Empty;

    [ObservableProperty]
    private ConfigChoice? _selectedCatalogueConfig = ConfigChoice.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfigManage))]
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

    // Transport (MTU / WebSocket proxy) editor for the config create form (#143): built by BeginSectionConfig
    // so the user can set MTU + proxy right when adding a config, applied to the just-created config on Save.
    [ObservableProperty]
    private ConfigTransportViewModel? _sectionConfigTransport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImportPicker))]
    [NotifyPropertyChangedFor(nameof(IsImportManual))]
    [NotifyPropertyChangedFor(nameof(IsImportCamera))]
    [NotifyPropertyChangedFor(nameof(SectionMethodLabel))]
    private ConfigImportMethod _importMethod = ConfigImportMethod.Picker;

    // Live QR scanner for the create form; non-null only while the camera method is active.
    [ObservableProperty]
    private ScanViewModel? _sectionScan;

    /// <summary>
    /// ctor
    /// </summary>
    public ConfigViewModel(MainWindowViewModel host, AgentConnection connection)
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

    // Landing on the Config section with nothing open: open the first config so it never opens empty. The active
    // profile's config is already reflected via OnOpenProfileChanged; this fills the gap when the profile has
    // none. A create-form draft in progress is left alone.
    public void SelectFirstIfNone()
    {
        if (OpenConfig is null && !IsCreatingSectionConfig && Configs.Count > 0)
        {
            OpenConfig = Configs[0].Name;
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
        ReconcileConfigCatalogueOptions();
        SyncCatalogueConfig();
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
            return;
        }

        var export = new ExportDialogViewModel(_connection, value);
        ConfigExport = export;
        _ = export.LoadAsync();

        var item = Configs.FirstOrDefault(c => string.Equals(c.Name, value, StringComparison.Ordinal));
        ConfigTransport = new ConfigTransportViewModel(_connection, value, item?.Endpoint ?? string.Empty, item?.UseWebSocket ?? false, item?.WebSocketHost ?? string.Empty, item?.WebSocketPort ?? 443, item?.Mtu ?? 0);
        ConfigTransport.AutoSave = true;
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
    }

    // The rename field changed: clear a stale validation line (#3). It autosaves on blur, not per keystroke.
    partial void OnConfigRenameChanged(string value)
    {
        ConfigRenameStatus = string.Empty;
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
        // Transport (MTU / WebSocket) editor so the proxy can be set right when adding a config (#143). The config
        // does not exist yet (no endpoint), so it seeds from defaults; Save retargets it at the final name and
        // applies it after the import. A left-at-defaults transport stays clean, so no set-websocket is sent.
        SectionConfigTransport = new ConfigTransportViewModel(_connection, SectionConfigName, string.Empty, false, string.Empty, 443, 0);
        ImportMethod = ConfigImportMethod.Picker;
        SectionScan = null;
        IsCreatingSectionConfig = true;
    }

    // Discards the create-form draft. Called by the header Cancel (#143 revert) and on disconnect.
    private void CancelSectionConfig()
    {
        IsCreatingSectionConfig = false;
        SectionConfigName = string.Empty;
        SectionConfigText = string.Empty;
        SectionConfigStatus = string.Empty;
        SectionConfigTransport = null;
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
        SectionScan = new ScanViewModel(ApplyScannedConfig);
        ImportMethod = ConfigImportMethod.Camera;
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

    // Footer Cancel: discard the draft and restore the config open before "+ Новая" (or «— не выбрано —»).
    [RelayCommand]
    private void CancelNewConfig()
    {
        CancelSectionConfig();
        OpenConfig = _configBeforeCreate;
    }

    // Footer Save: import the drafted config.
    [RelayCommand]
    private async Task SaveNewConfig()
    {
        await SaveSectionConfig();
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

    // Commits the open config's dirty fields when focus leaves one (autosave, existing config only). A blur that
    // arrives mid-commit re-runs once the in-flight commit settles, so the last field is never dropped.
    public async void AutoSaveOnBlur()
    {
        if (OpenConfig is null)
        {
            return;
        }

        if (_autoSaving)
        {
            _autoSavePending = true;
            return;
        }

        _autoSaving = true;
        try
        {
            do
            {
                _autoSavePending = false;

                if (ConfigExport is { IsDirty: true } export)
                {
                    await export.CommitAsync();
                }

                if (ConfigTransport is { } transport)
                {
                    await transport.AutoSaveAsync();
                }

                // Rename last so the .conf / transport ops still key by the old name.
                if (!string.Equals(ConfigRename ?? string.Empty, _baseConfigRename, StringComparison.Ordinal))
                {
                    await CommitConfigRenameAsync();
                }
            }
            while (_autoSavePending);
        }
        finally
        {
            _autoSaving = false;
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

            // Apply the create-form transport (MTU / WebSocket proxy) to the just-created config. Only when the
            // user actually touched it (a defaults-only editor stays clean). The config exists now, so a rejected
            // transport does not undo the import - surface it as a notice and still open the config to fix there.
            if (SectionConfigTransport is { IsDirty: true } transport)
            {
                transport.Retarget(name);
                if (!await transport.CommitAsync())
                {
                    _host.Home.ShowNotice(transport.StatusMessage);
                }
            }

            IsCreatingSectionConfig = false;
            SectionConfigName = string.Empty;
            SectionConfigText = string.Empty;
            SectionConfigStatus = string.Empty;
            SectionConfigTransport = null;
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
