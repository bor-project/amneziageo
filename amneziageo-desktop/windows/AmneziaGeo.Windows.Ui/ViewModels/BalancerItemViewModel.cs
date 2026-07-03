using System.Collections.ObjectModel;
using Avalonia.Media;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
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
    private readonly Func<string, bool, Task> _setProfileConnection;
    private readonly Func<string, Task<IpcAck>> _removeConfig;
    // Picking the "+ Новая конфигурация" / "+ Новый список" sentinel in this profile's combos does not open
    // an inline form; it redirects the host to the Config / Routing settings section with the create UI
    // opened there (the combo snaps back to the profile's current selection). Supplied by the host.
    private readonly Action _onRequestNewConfig;
    private readonly Action _onRequestNewList;
    private bool _suppress;
    // One-shot flag: set before RoutingListOptions.Clear() so the deferred Avalonia binding
    // event (SelectedItem → None) that arrives after _suppress=false is consumed and ignored.
    private bool _suppressNextRoutingNone;
    // Optimistic running override: set the instant the user taps connect/disconnect on this profile so the
    // button flips immediately; cleared once a snapshot's real status agrees with the requested state.
    private bool? _pendingRunning;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(HasConfig))]
    [NotifyPropertyChangedFor(nameof(IsComplete))]
    private string _config = string.Empty;

    // The config selected in the profile's config combo. Picking a real config assigns it to this profile
    // (configs are shared across profiles by name); picking "+ Новая конфигурация" redirects to the Config
    // section to add one there.
    [ObservableProperty]
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

    /// <summary>
    /// ctor
    /// </summary>
    public BalancerItemViewModel(
        Func<string, string, Task> saveProfile,
        Func<string, long?, bool, Task> assignRouting,
        Func<string, Task> selectProfile,
        Func<string, bool, Task> setProfileConnection,
        Func<string, Task<IpcAck>> removeConfig,
        Action onRequestNewConfig,
        Action onRequestNewList)
    {
        _saveProfile = saveProfile;
        _assignRouting = assignRouting;
        _selectProfile = selectProfile;
        _setProfileConnection = setProfileConnection;
        _removeConfig = removeConfig;
        _onRequestNewConfig = onRequestNewConfig;
        _onRequestNewList = onRequestNewList;
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
    public string ConnectActionText => OtherActive ? Loc.Instance.Get("Balancer_SwitchAction") : Loc.Instance.Get("Balancer_ConnectAction");

    /// <summary>
    /// True when a real routing list is selected and the toggle can flip use_routing on.
    /// </summary>
    public bool CanToggleRouting => SelectedRoutingList.IsReal;

    /// <summary>
    /// Whether the profile has a configuration assigned.
    /// </summary>
    public bool HasConfig => Config.Length > 0;

    /// <summary>
    /// Whether the profile is complete enough to dial: a configuration must be assigned (there is nothing to
    /// connect to without one). Routing always has a valid value - «Полный туннель» when no list is chosen -
    /// so it never blocks completeness.
    /// </summary>
    public bool IsComplete => HasConfig;

    /// <summary>
    /// Collapsed-row summary: the configuration name, or a hint when none is set yet.
    /// </summary>
    public string Detail => HasConfig ? Config : Loc.Instance.Get("Balancer_NoConfig");

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
            // it as a transient real choice (appended after the None/New head and the saved configs) so the
            // combo shows the real name instead of misrepresenting a set config as «— не выбрано —» - a
            // divergence a later combo touch could otherwise turn into an accidental unassign.
            var effectiveOptions = configOptions;
            if (entry.Config.Length > 0
                && !configOptions.Any(option => option.IsReal && string.Equals(option.Name, entry.Config, StringComparison.Ordinal)))
            {
                var augmented = configOptions.ToList();
                augmented.Add(new ConfigChoice(entry.Config));
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

            // Resolve the combo to the profile's assigned config, or «— не выбрано —» when it has none.
            SelectedConfig = ConfigOptions.FirstOrDefault(
                    option => option.IsReal && string.Equals(option.Name, entry.Config, StringComparison.Ordinal))
                ?? ConfigChoice.None;

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

            // Resolve the combo to the profile's assigned list, or «Полный туннель» (the None sentinel) when
            // it has none. The "+ Новый список" pick is never held across snapshots now - it redirects to the
            // Routing section instead of opening an inline editor.
            SelectedRoutingList = RoutingListOptions.FirstOrDefault(option => option.Id == entry.RoutingListId) ?? RoutingListChoice.None;

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

    partial void OnSelectedConfigChanged(ConfigChoice? oldValue, ConfigChoice newValue)
    {
        // newValue can momentarily be null: clearing the bound options nulls the ComboBox's SelectedItem.
        if (_suppress || newValue is null)
        {
            return;
        }

        if (newValue.IsNewSentinel)
        {
            // "+ Новая конфигурация" is a redirect, not an inline form: send the host to the Config section
            // to add one there, then snap this combo back to whatever the profile currently has (guarded so
            // the revert issues no redundant IPC). The user re-selects the new config here once it exists.
            _onRequestNewConfig();
            RevertSelectedConfig();
            return;
        }

        if (newValue.IsReal && !string.Equals(newValue.Name, Config, StringComparison.Ordinal))
        {
            // Picking an existing config (re)assigns it to this profile - the same config can back several
            // profiles, since balancers reference a config by name.
            Config = newValue.Name;
            _ = _saveProfile(Name, newValue.Name);
        }
        else if (newValue.IsNone && Config.Length > 0)
        {
            Config = string.Empty;
            _ = _saveProfile(Name, string.Empty);
        }
    }

    // Snap the config combo back to the profile's current config (or «— не выбрано —»), under _suppress so
    // the re-selection does not re-enter the assign path.
    private void RevertSelectedConfig()
    {
        _suppress = true;
        SelectedConfig = ConfigOptions.FirstOrDefault(
                option => option.IsReal && string.Equals(option.Name, Config, StringComparison.Ordinal))
            ?? ConfigChoice.None;
        _suppress = false;
    }

    partial void OnSelectedRoutingListChanged(RoutingListChoice? oldValue, RoutingListChoice newValue)
    {
        // newValue can momentarily be null: clearing the bound options nulls the ComboBox's SelectedItem.
        if (_suppress || newValue is null)
        {
            return;
        }

        // RoutingListOptions.Clear() inside ApplyFromEntry posts a deferred SelectedItem→None event
        // that the Avalonia binding dispatcher delivers after _suppress=false. Without this guard it
        // would call _assignRouting(null) and unassign the routing list mid-edit. The one-shot flag is
        // armed only when there is an active real/new-sentinel selection to protect.
        if (!newValue.IsReal && !newValue.IsNewSentinel && _suppressNextRoutingNone)
        {
            _suppressNextRoutingNone = false;
            return;
        }
        _suppressNextRoutingNone = false;

        if (newValue.IsNewSentinel)
        {
            // "+ Новый список" is a redirect, not an inline editor: send the host to the Routing section to
            // create one there, then snap this combo back to the profile's current list (guarded), so the
            // sentinel does not stick. The user re-selects the new list here once it exists.
            _onRequestNewList();
            _suppress = true;
            SelectedRoutingList = oldValue ?? RoutingListChoice.None;
            _suppress = false;
            return;
        }

        if (!newValue.IsReal && UseRouting)
        {
            UseRouting = false;
        }

        _ = _assignRouting(Name, newValue.IsReal ? newValue.Id : null, UseRouting && newValue.IsReal);
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
