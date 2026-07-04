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
    private bool _suppress;
    private bool _suppressNextRoutingNone;
    private bool? _pendingRunning;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(HasConfig))]
    [NotifyPropertyChangedFor(nameof(IsComplete))]
    private string _config = string.Empty;

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
        Func<string, Task<IpcAck>> removeConfig)
    {
        _saveProfile = saveProfile;
        _assignRouting = assignRouting;
        _selectProfile = selectProfile;
        _setProfileConnection = setProfileConnection;
        _removeConfig = removeConfig;
    }

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
    /// connect vs disconnect affordance.
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

            // Keep the assigned config selectable even if it's not in the catalogue.
            var effectiveOptions = configOptions;
            if (entry.Config.Length > 0
                && !configOptions.Any(option => option.IsReal && string.Equals(option.Name, entry.Config, StringComparison.Ordinal)))
            {
                var augmented = configOptions.ToList();
                augmented.Add(new ConfigChoice(entry.Config));
                effectiveOptions = augmented;
            }

            if (!ConfigOptions.SequenceEqual(effectiveOptions))
            {
                ConfigOptions.Clear();
                foreach (var option in effectiveOptions)
                {
                    ConfigOptions.Add(option);
                }
            }

            SelectedConfig = ConfigOptions.FirstOrDefault(
                    option => option.IsReal && string.Equals(option.Name, entry.Config, StringComparison.Ordinal))
                ?? ConfigChoice.None;

            if (!RoutingListOptions.SequenceEqual(routingOptions))
            {
                // Arm the one-shot flag before Clear() fires a None echo.
                _suppressNextRoutingNone = SelectedRoutingList.IsReal;
                RoutingListOptions.Clear();
                foreach (var option in routingOptions)
                {
                    RoutingListOptions.Add(option);
                }
            }

            SelectedRoutingList = RoutingListOptions.FirstOrDefault(option => option.Id == entry.RoutingListId) ?? RoutingListChoice.None;

            UseRouting = entry.UseRouting && SelectedRoutingList.IsReal;
        }
        finally
        {
            _suppress = false;
        }

        // Clear the optimistic status once the real status matches.
        var statusRunning = !string.Equals(Status, ConnectionStatus.Disconnected, StringComparison.Ordinal);
        if (_pendingRunning == statusRunning)
        {
            _pendingRunning = null;
        }

        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(IsRunning));
    }

    /// <summary>
    /// Remove the config from the catalogue and clear it from this profile.
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
        // newValue can be null while the options are being cleared.
        if (_suppress || newValue is null)
        {
            return;
        }

        if (newValue.IsReal && !string.Equals(newValue.Name, Config, StringComparison.Ordinal))
        {
            Config = newValue.Name;
            _ = _saveProfile(Name, newValue.Name);
        }
        else if (newValue.IsNone && Config.Length > 0)
        {
            Config = string.Empty;
            _ = _saveProfile(Name, string.Empty);
        }
    }

    partial void OnSelectedRoutingListChanged(RoutingListChoice? oldValue, RoutingListChoice newValue)
    {
        // newValue can be null while the options are being cleared.
        if (_suppress || newValue is null)
        {
            return;
        }

        // Swallow the Clear() echo in the routing combo.
        if (!newValue.IsReal && _suppressNextRoutingNone)
        {
            _suppressNextRoutingNone = false;
            return;
        }
        _suppressNextRoutingNone = false;

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
