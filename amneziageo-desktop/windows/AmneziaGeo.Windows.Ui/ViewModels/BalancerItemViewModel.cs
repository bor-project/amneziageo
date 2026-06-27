using System.Collections.ObjectModel;
using Avalonia.Media;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// A single profile row in the list: it owns exactly one configuration plus its routing-list assignment
/// and connection state. Mutations are dispatched as IPC commands to the agent via the provided delegates.
/// </summary>
internal sealed partial class BalancerItemViewModel : ViewModelBase
{
    private readonly Func<string, string, Task> _saveProfile;
    private readonly Func<string, long?, bool, Task> _assignRouting;
    private readonly Func<string, Task> _selectProfile;
    private readonly Func<string, string, Task<IpcAck>> _importConfig;
    private readonly Func<string, bool, Task> _setProfileConnection;
    private readonly Func<string, Task<IpcAck>> _removeConfig;
    private readonly Func<string, string, Task<IpcAck>> _copyConfig;
    private bool _suppress;
    // True while the user is composing a brand-new config via the "+ Новая конфигурация" combo pick; held
    // across snapshots (like _creatingNewList) so the periodic reconcile does not snap the config combo back
    // to the config the agent still has assigned while the inline import form is open.
    private bool _creatingNewConfig;
    // True while the user is building a brand-new routing list ("+ Новый список"). Set by the combo pick,
    // cleared when another option is chosen; snapshots honour it so the periodic reconcile does not snap the
    // selection back to the list the agent still has assigned. Once the new list is saved its id is recorded
    // in _savedNewListId, and the hold is released only when a snapshot reports THAT list assigned.
    private bool _creatingNewList;
    private long? _savedNewListId;
    // One-shot flag: set before RoutingListOptions.Clear() so the deferred Avalonia binding
    // event (SelectedItem → None) that arrives after _suppress=false is consumed and ignored.
    // Same root cause as the ConfigOptions/OnSelectedConfigChanged issue fixed for #57.
    private bool _suppressNextRoutingNone;

    /// <summary>True while the deferred Avalonia ComboBox None/null event from a RoutingListOptions
    /// rebuild (Clear + repopulate) is still in flight. Read by MainWindowViewModel.SyncProfileRoutingEditor
    /// to avoid closing the inline rule editor during a snapshot reconcile — the deferred None is an
    /// artifact, not a real user deselection.</summary>
    public bool IsRoutingListRebuildPending => _suppressNextRoutingNone;
    // Optimistic running override: set the instant the user taps connect/disconnect on this profile so the
    // button flips immediately; cleared once a snapshot's real status agrees with the requested state.
    private bool? _pendingRunning;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(HasConfig))]
    private string _config = string.Empty;

    // The config selected in the profile's config combo. Picking a real config assigns it to this profile
    // (configs are shared across profiles by name); picking "+ Новая конфигурация" reveals the import form.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDuplicateConfig))]
    private ConfigChoice _selectedConfig = ConfigChoice.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    private string _status = ConnectionStatus.Disconnected;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectActionText))]
    private bool _otherActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleRouting))]
    private RoutingListChoice _selectedRoutingList = RoutingListChoice.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleRouting))]
    private bool _useRouting;

    // Inline "new config" form: shown to set (or replace) this profile's single configuration.
    [ObservableProperty]
    private bool _isCreatingConfig;

    [ObservableProperty]
    private string _newConfigName = string.Empty;

    [ObservableProperty]
    private string _newConfigText = string.Empty;

    [ObservableProperty]
    private string _newConfigStatus = string.Empty;

    /// <summary>
    /// ctor
    /// </summary>
    public BalancerItemViewModel(
        Func<string, string, Task> saveProfile,
        Func<string, long?, bool, Task> assignRouting,
        Func<string, Task> selectProfile,
        Func<string, string, Task<IpcAck>> importConfig,
        Func<string, bool, Task> setProfileConnection,
        Func<string, Task<IpcAck>> removeConfig,
        Func<string, string, Task<IpcAck>> copyConfig)
    {
        _saveProfile = saveProfile;
        _assignRouting = assignRouting;
        _selectProfile = selectProfile;
        _importConfig = importConfig;
        _setProfileConnection = setProfileConnection;
        _removeConfig = removeConfig;
        _copyConfig = copyConfig;
    }

    /// <summary>
    /// The config options available in the profile's config combo: the synthetic "- не задан -",
    /// every config in the shared catalogue (selectable / reusable across profiles), then the trailing
    /// "+ Новая конфигурация" sentinel. Rebuilt from the snapshot in <see cref="ApplyFromEntry"/>.
    /// </summary>
    public ObservableCollection<ConfigChoice> ConfigOptions { get; } = [];

    /// <summary>
    /// The routing-list options available in the combo box.
    /// </summary>
    public ObservableCollection<RoutingListChoice> RoutingListOptions { get; } = [];

    /// <summary>
    /// The localized status label.
    /// </summary>
    public string StatusText => StatusLabels.Text(Status);

    /// <summary>
    /// The status badge color.
    /// </summary>
    public IBrush StatusBrush => StatusLabels.Brush(Status);

    /// <summary>
    /// Whether this profile is the one the agent currently has up. Drives the per-profile
    /// Подключить vs Отключить affordance.
    /// </summary>
    public bool IsRunning => _pendingRunning ?? !string.Equals(Status, ConnectionStatus.Disconnected, StringComparison.Ordinal);

    /// <summary>
    /// Label for the per-profile connect button: "Переключить" when a DIFFERENT profile is the live tunnel
    /// (tapping switches to this one), otherwise "Подключить".
    /// </summary>
    public string ConnectActionText => OtherActive ? "Переключить" : "Подключить";

    /// <summary>
    /// True when a real routing list is selected and the toggle can flip use_routing on.
    /// </summary>
    public bool CanToggleRouting => SelectedRoutingList.IsReal;

    /// <summary>
    /// Whether the profile has a configuration assigned.
    /// </summary>
    public bool HasConfig => Config.Length > 0;

    /// <summary>True when a real config is selected and "Дублировать" can fork an independent copy.</summary>
    public bool CanDuplicateConfig => SelectedConfig.IsReal;

    /// <summary>
    /// Collapsed-row summary: the configuration name, or a hint when none is set yet.
    /// </summary>
    public string Detail => HasConfig ? Config : "нет конфигурации";

    [RelayCommand]
    private Task Select()
    {
        return _selectProfile(Name);
    }

    // Connect THIS profile: select it as the target, then connect. If another profile is already up, the
    // agent's supervisor re-latches the target on connect and re-runs the loop - tearing the old tunnel
    // down and bringing this one up (an automatic switch).
    [RelayCommand]
    private Task ConnectProfile()
    {
        SetPendingRunning(true);
        return _setProfileConnection(Name, true);
    }

    [RelayCommand]
    private Task DisconnectProfile()
    {
        SetPendingRunning(false);
        return _setProfileConnection(Name, false);
    }

    private void SetPendingRunning(bool value)
    {
        _pendingRunning = value;
        OnPropertyChanged(nameof(IsRunning));
    }

    /// <summary>
    /// Populates view-model state from the agent-side entry without firing change-driven IPC calls.
    /// </summary>
    public void ApplyFromEntry(
        BalancerEntry entry,
        IReadOnlyList<RoutingListChoice> routingOptions,
        IReadOnlyList<ConfigChoice> configOptions)
    {
        _suppress = true;
        try
        {
            Name = entry.Name;
            Status = entry.Status;
            Config = entry.Config;

            // The profile's assigned config must always be selectable, even if it is momentarily absent from
            // the shared catalogue (e.g. a config it reuses was renamed/removed via another profile). Surface
            // it as a transient real choice so the combo shows the real name instead of misrepresenting a set
            // config as "- не задан -" - a divergence a later combo touch could otherwise turn into an
            // accidental unassign.
            var effectiveOptions = configOptions;
            if (entry.Config.Length > 0
                && !configOptions.Any(option => option.IsReal && string.Equals(option.Name, entry.Config, StringComparison.Ordinal)))
            {
                var augmented = configOptions.ToList();
                augmented.Insert(Math.Max(0, augmented.Count - 1), new ConfigChoice(entry.Config));
                effectiveOptions = augmented;
            }

            // Rebuild config options only when the catalogue changed (same reasoning as routing below).
            if (!ConfigOptions.SequenceEqual(effectiveOptions))
            {
                ConfigOptions.Clear();
                foreach (var option in effectiveOptions)
                {
                    ConfigOptions.Add(option);
                }
            }

            // While composing a new config (the inline import form is open), hold the "+ Новая
            // конфигурация" pick across snapshots; otherwise resolve the combo to the profile's assigned
            // config, or "- не задан -" when it has none.
            if (_creatingNewConfig && IsCreatingConfig)
            {
                // Avoid a redundant assignment: setting the same sentinel fires a property-changed
                // notification that Avalonia can deliver asynchronously, becoming the spurious event
                // OnSelectedConfigChanged now guards against. Skip if already correct.
                if (SelectedConfig != ConfigChoice.NewConfig)
                {
                    SelectedConfig = ConfigChoice.NewConfig;
                }
            }
            else
            {
                _creatingNewConfig = false;
                SelectedConfig = ConfigOptions.FirstOrDefault(
                        option => option.IsReal && string.Equals(option.Name, entry.Config, StringComparison.Ordinal))
                    ?? ConfigChoice.None;
            }

            // Rebuild the options only when the catalogue actually changed. A Clear()+Add() on every
            // snapshot would null the ComboBox's selection (which then churns the inline editor) even
            // when nothing changed.
            if (!RoutingListOptions.SequenceEqual(routingOptions))
            {
                // Arm the one-shot flag before Clear() so the deferred Avalonia ComboBox
                // SelectedItem→None event that arrives after _suppress=false is swallowed.
                // Only arm when the current selection has something worth protecting.
                _suppressNextRoutingNone = SelectedRoutingList.IsReal || SelectedRoutingList.IsNewSentinel;
                RoutingListOptions.Clear();
                foreach (var option in routingOptions)
                {
                    RoutingListOptions.Add(option);
                }
            }

            var resolved = RoutingListOptions.FirstOrDefault(option => option.Id == entry.RoutingListId) ?? RoutingListChoice.None;
            // While the user is building a new list, hold the "+ Новый список" pick across snapshots - do NOT
            // snap back to the list the agent still has assigned. Release the hold only once the snapshot
            // actually reports the freshly-saved new list assigned.
            if (_creatingNewList && !(_savedNewListId is long saved && resolved.Id == saved))
            {
                SelectedRoutingList = RoutingListChoice.NewList;
            }
            else
            {
                _creatingNewList = false;
                _savedNewListId = null;
                SelectedRoutingList = resolved;
            }

            UseRouting = entry.UseRouting && SelectedRoutingList.IsReal;
        }
        finally
        {
            _suppress = false;
        }

        // Release the optimistic running override once the real status agrees with the user's last tap.
        var statusRunning = !string.Equals(Status, ConnectionStatus.Disconnected, StringComparison.Ordinal);
        if (_pendingRunning == statusRunning)
        {
            _pendingRunning = null;
        }

        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(IsRunning));
    }

    /// <summary>
    /// Deletes this profile's configuration from the catalogue and clears it from the profile. The agent
    /// refuses while the config is in use by the running profile, so on a non-OK ack nothing changes.
    /// Returns the agent's ack so the caller can report a refusal.
    /// </summary>
    public async Task<IpcAck> DeleteConfigAsync(string config)
    {
        var ack = await _removeConfig(config);
        if (!ack.Ok)
        {
            return ack;
        }

        Config = string.Empty;
        await _saveProfile(Name, string.Empty);
        return ack;
    }

    // Reveal the inline form to set (or replace) this profile's configuration.
    [RelayCommand]
    private void BeginAddConfig()
    {
        IsCreatingConfig = true;
    }

    [RelayCommand]
    private async Task SaveNewConfig()
    {
        var imported = VpnLinkCodec.TryDecode(NewConfigText);
        if (imported is null)
        {
            NewConfigStatus = "Не распознано (.conf или vpn://)";
            return;
        }

        var name = !string.IsNullOrWhiteSpace(NewConfigName) ? NewConfigName.Trim() : DefaultConfigName(imported);

        var ack = await _importConfig(name, imported.ConfText);
        if (!ack.Ok)
        {
            NewConfigStatus = ack.Message;
            return;
        }

        Config = name;
        await _saveProfile(Name, name);
        ResetNewConfig();
    }

    [RelayCommand]
    private void CancelNewConfig()
    {
        ResetNewConfig();
        // Snap the combo off the "+ Новая конфигурация" pick back to the profile's current config: a pure
        // cancel issues no command, so no snapshot follows to resolve the selection on its own.
        _creatingNewConfig = false;
        SelectedConfig = ConfigOptions.FirstOrDefault(
                option => option.IsReal && string.Equals(option.Name, Config, StringComparison.Ordinal))
            ?? ConfigChoice.None;
    }

    private void ResetNewConfig()
    {
        IsCreatingConfig = false;
        NewConfigName = string.Empty;
        NewConfigText = string.Empty;
        NewConfigStatus = string.Empty;
    }

    // The default name for an imported config when the user typed none and no file name was carried in (a
    // pasted vpn:// link or bare text): the tunnel Address from the config, then any name the share link
    // described, then a generic placeholder. A file import already pre-fills NewConfigName with the file name.
    private static string DefaultConfigName(VpnLinkCodec.Imported imported)
    {
        var address = ParseInterfaceAddress(imported.ConfText);
        if (!string.IsNullOrWhiteSpace(address))
        {
            return address!;
        }

        return !string.IsNullOrWhiteSpace(imported.Name) ? imported.Name!.Trim() : "config";
    }

    // Extracts the [Interface] Address value (first entry, mask stripped) from wg-quick text, e.g.
    // "Address = 10.8.0.2/32, fd00::2/128" -> "10.8.0.2"; null when there is no Address line.
    private static string? ParseInterfaceAddress(string confText)
    {
        foreach (var raw in confText.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Address", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var first = line[(eq + 1)..].Split(',')[0].Trim();
            var slash = first.IndexOf('/');
            return slash > 0 ? first[..slash] : first;
        }

        return null;
    }

    /// <summary>
    /// Called once the in-progress new list has been saved with a real id (and is being assigned to this
    /// profile). The "+ Новый список" hold is kept until a snapshot reports this id assigned, then released.
    /// </summary>
    public void NotifyNewListSaved(long id)
    {
        _savedNewListId = id;
    }

    partial void OnSelectedConfigChanged(ConfigChoice value)
    {
        // value can momentarily be null: clearing the bound options nulls the ComboBox's SelectedItem.
        if (_suppress || value is null)
        {
            return;
        }

        // The config combo is hidden while the new-config form is open (IsVisible=!IsCreatingConfig).
        // Any non-sentinel value arriving here while the form is open is a spurious Avalonia binding
        // event from the ObservableCollection Clear/Add in ApplyFromEntry: the dispatcher delivers
        // SelectedItem-changed notifications AFTER _suppress is released, momentarily resolving to
        // None. Block it so the form stays open and the typed name is preserved.
        if (_creatingNewConfig && IsCreatingConfig && !value.IsNewSentinel)
        {
            return;
        }

        _creatingNewConfig = value.IsNewSentinel;

        if (value.IsNewSentinel)
        {
            // Reveal the inline import form; the hold keeps this pick across snapshots until the new config
            // is imported and assigned (SaveNewConfig), after which a snapshot resolves the combo to it.
            IsCreatingConfig = true;
            return;
        }

        // Leaving the "+ Новая конфигурация" pick collapses the inline import form.
        if (IsCreatingConfig)
        {
            ResetNewConfig();
        }

        if (value.IsReal && !string.Equals(value.Name, Config, StringComparison.Ordinal))
        {
            // Picking an existing config (re)assigns it to this profile - the same config can back several
            // profiles, since balancers reference a config by name.
            Config = value.Name;
            _ = _saveProfile(Name, value.Name);
        }
        else if (value.IsNone && Config.Length > 0)
        {
            Config = string.Empty;
            _ = _saveProfile(Name, string.Empty);
        }
    }

    // Fork an independent copy of the profile's current config (text + geo + resolutions) under a fresh
    // name and switch this profile to the copy, so it can be edited without touching the shared original.
    [RelayCommand]
    private async Task DuplicateConfig()
    {
        if (!SelectedConfig.IsReal)
        {
            return;
        }

        var source = SelectedConfig.Name;
        var destination = UniqueCopyName(source);
        var ack = await _copyConfig(source, destination);
        if (!ack.Ok)
        {
            NewConfigStatus = ack.Message;
            return;
        }

        Config = destination;
        await _saveProfile(Name, destination);
    }

    // "<name> (копия)", then " (копия 2)", ... avoiding any name already in the catalogue.
    private string UniqueCopyName(string source)
    {
        var taken = ConfigOptions.Where(option => option.IsReal).Select(option => option.Name).ToHashSet(StringComparer.Ordinal);
        var baseName = $"{source} (копия)";
        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{source} (копия {i})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    partial void OnSelectedRoutingListChanged(RoutingListChoice value)
    {
        // value can momentarily be null: clearing the bound options nulls the ComboBox's SelectedItem.
        if (_suppress || value is null)
        {
            return;
        }

        // RoutingListOptions.Clear() inside ApplyFromEntry posts a deferred SelectedItem→None event
        // that the Avalonia binding dispatcher delivers after _suppress=false. Without this guard it
        // would call _assignRouting(null) and unassign the routing list mid-edit, closing the editor.
        // The one-shot flag is armed only when there is an active real/new-sentinel selection to protect.
        if (!value.IsReal && !value.IsNewSentinel && _suppressNextRoutingNone)
        {
            _suppressNextRoutingNone = false;
            return;
        }
        _suppressNextRoutingNone = false;

        _creatingNewList = value.IsNewSentinel;
        if (!value.IsNewSentinel)
        {
            _savedNewListId = null;
        }

        if (value.IsNewSentinel)
        {
            if (UseRouting)
            {
                UseRouting = false;
            }

            return;
        }

        if (!value.IsReal && UseRouting)
        {
            UseRouting = false;
        }

        _ = _assignRouting(Name, value.IsReal ? value.Id : null, UseRouting && value.IsReal);
    }

    partial void OnUseRoutingChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        if (value && SelectedRoutingList.IsNone)
        {
            UseRouting = false;
            return;
        }

        _ = _assignRouting(Name, SelectedRoutingList.Id, value);
    }
}
