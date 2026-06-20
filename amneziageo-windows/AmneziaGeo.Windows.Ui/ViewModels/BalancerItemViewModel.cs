using System.Collections.ObjectModel;
using Avalonia.Media;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// A single profile (balancer group) row in the list, with its expanded editor state.
/// Mutations are dispatched as IPC commands to the agent via the provided action delegates.
/// </summary>
internal sealed partial class BalancerItemViewModel : ViewModelBase
{
    /// <summary>The sentinel combo-box item that opens the inline "new config" form.</summary>
    public const string NewConfigOption = "+ Новая конфигурация";

    private readonly Func<string, int, string, IReadOnlyList<string>, Task> _saveBalancer;
    private readonly Func<string, long?, bool, Task> _assignRouting;
    private readonly Func<string, Task> _selectProfile;
    private readonly Func<string, string, Task<IpcAck>> _importConfig;
    private readonly Func<string, bool, Task> _setProfileConnection;
    private readonly Func<string, Task<IpcAck>> _removeConfig;
    private bool _suppress;
    // Optimistic running override: set the instant the user taps connect/disconnect on this profile so the
    // button flips immediately (like the header power control); cleared once a snapshot's real status
    // agrees with the requested state.
    private bool? _pendingRunning;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(IsOffMode))]
    [NotifyPropertyChangedFor(nameof(IsPriorityMode))]
    [NotifyPropertyChangedFor(nameof(IsLatencyMode))]
    [NotifyPropertyChangedFor(nameof(ShowInterval))]
    private string _mode = "priority";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(IsInterval10))]
    [NotifyPropertyChangedFor(nameof(IsInterval30))]
    [NotifyPropertyChangedFor(nameof(IsInterval60))]
    private int _recheckSeconds = 60;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    private string _status = ConnectionStatus.Disconnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string? _activeMember;

    [ObservableProperty]
    private bool _isExpanded;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddMember))]
    private string? _addMemberSelection;

    // Inline "new config" form, shown under the combo box when the sentinel item is selected.
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
        Func<string, int, string, IReadOnlyList<string>, Task> saveBalancer,
        Func<string, long?, bool, Task> assignRouting,
        Func<string, Task> selectProfile,
        Func<string, string, Task<IpcAck>> importConfig,
        Func<string, bool, Task> setProfileConnection,
        Func<string, Task<IpcAck>> removeConfig)
    {
        _saveBalancer = saveBalancer;
        _assignRouting = assignRouting;
        _selectProfile = selectProfile;
        _importConfig = importConfig;
        _setProfileConnection = setProfileConnection;
        _removeConfig = removeConfig;

        // Adding/removing/reordering a member flips whether the balancer is available (>1 config) and
        // which member is the default (first), so refresh the dependent state when membership changes.
        Members.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanUseBalancer));
            OnPropertyChanged(nameof(IsOffMode));
            OnPropertyChanged(nameof(IsPriorityMode));
            OnPropertyChanged(nameof(IsLatencyMode));
            OnPropertyChanged(nameof(ShowInterval));
            OnPropertyChanged(nameof(Detail));
            OnPropertyChanged(nameof(DefaultMember));
        };
    }

    /// <summary>
    /// The ordered members of the profile (configs).
    /// </summary>
    public ObservableCollection<string> Members { get; } = [];

    /// <summary>
    /// The routing-list options available in the combo box.
    /// </summary>
    public ObservableCollection<RoutingListChoice> RoutingListOptions { get; } = [];

    /// <summary>
    /// Configs available to be added as new members (not yet in this profile).
    /// </summary>
    public ObservableCollection<string> AvailableConfigs { get; } = [];

    /// <summary>
    /// The localized status label.
    /// </summary>
    public string StatusText => StatusLabels.Text(Status);

    /// <summary>
    /// The status badge color.
    /// </summary>
    public IBrush StatusBrush => StatusLabels.Brush(Status);

    /// <summary>
    /// Whether this profile is the one the agent currently has up (the runner serves exactly one target
    /// at a time, so any non-disconnected status means this profile is the live/connecting one). Drives
    /// the per-profile Подключить vs Отключить affordance.
    /// </summary>
    public bool IsRunning => _pendingRunning ?? !string.Equals(Status, ConnectionStatus.Disconnected, StringComparison.Ordinal);

    /// <summary>
    /// Label for the per-profile connect button: "Переключить" when a DIFFERENT profile is the live tunnel
    /// (tapping switches to this one), otherwise "Подключить".
    /// </summary>
    public string ConnectActionText => OtherActive ? "Переключить" : "Подключить";

    /// <summary>
    /// True when a real routing list is selected and the toggle can flip use_routing on. The "new list"
    /// sentinel does not count — there is nothing to route to until it is named and saved.
    /// </summary>
    public bool CanToggleRouting => SelectedRoutingList.IsReal;

    /// <summary>
    /// Whether the "Добавить" button is actionable: a real config (not the "new config" sentinel) is
    /// selected. Disabled when nothing is selected.
    /// </summary>
    public bool CanAddMember => !string.IsNullOrEmpty(AddMemberSelection) && AddMemberSelection != NewConfigOption;

    /// <summary>
    /// Whether the balancer can be used at all: only with more than one config. With 0 or 1 config the
    /// balancer controls are disabled and the group runs as a plain single tunnel.
    /// </summary>
    public bool CanUseBalancer => Members.Count > 1;

    /// <summary>
    /// The config used by default: the first member. With the balancer off (or a single config) the
    /// agent pins to the first member, and in priority mode the first member is the preferred one — so
    /// "default" is always the head of the list. The per-config radio picks it (moves it to the front).
    /// </summary>
    public string? DefaultMember => Members.Count > 0 ? Members[0] : null;

    /// <summary>
    /// Whether the balancer is off ("don't use"): the user chose it, or it's forced off by having one
    /// config or none.
    /// </summary>
    public bool IsOffMode => !CanUseBalancer || Mode == "off";

    /// <summary>
    /// Whether the profile uses the priority-order balancer mode (only meaningful when balancing).
    /// </summary>
    public bool IsPriorityMode => CanUseBalancer && Mode == "priority";

    /// <summary>
    /// Whether the profile uses the best-availability balancer mode (only meaningful when balancing).
    /// </summary>
    public bool IsLatencyMode => CanUseBalancer && Mode == "latency";

    /// <summary>
    /// Whether the recheck-interval row is relevant: shown only while actually balancing (probing).
    /// </summary>
    public bool ShowInterval => CanUseBalancer && Mode != "off";

    /// <summary>
    /// Whether the recheck interval is at the 10-second preset.
    /// </summary>
    public bool IsInterval10 => RecheckSeconds == 10;

    /// <summary>
    /// Whether the recheck interval is at the 30-second preset.
    /// </summary>
    public bool IsInterval30 => RecheckSeconds == 30;

    /// <summary>
    /// Whether the recheck interval is at the 60-second preset.
    /// </summary>
    public bool IsInterval60 => RecheckSeconds == 60;

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }

    [RelayCommand]
    private Task Select()
    {
        return _selectProfile(Name);
    }

    // Connect THIS profile: select it as the target, then connect. If another profile is already up, the
    // agent's supervisor re-latches the target on connect and re-runs the loop — tearing the old tunnel
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

    [RelayCommand]
    private void SetOffMode()
    {
        Mode = "off";
    }

    [RelayCommand]
    private void SetPriorityMode()
    {
        Mode = "priority";
    }

    [RelayCommand]
    private void SetLatencyMode()
    {
        Mode = "latency";
    }

    [RelayCommand]
    private void SetInterval10()
    {
        RecheckSeconds = 10;
    }

    [RelayCommand]
    private void SetInterval30()
    {
        RecheckSeconds = 30;
    }

    [RelayCommand]
    private void SetInterval60()
    {
        RecheckSeconds = 60;
    }

    /// <summary>
    /// Collapsed-row summary like "по приоритету · активен: m0 · проверка: 60с · серверов: 2".
    /// </summary>
    public string Detail
    {
        get
        {
            var active = ActiveMember ?? "—";
            if (IsOffMode)
            {
                return $"без балансировки · активен: {active} · серверов: {Members.Count}";
            }

            var mode = Mode == "latency" ? "по задержке" : "по приоритету";
            return $"{mode} · активен: {active} · проверка: {RecheckSeconds}с · серверов: {Members.Count}";
        }
    }

    /// <summary>
    /// Populates view-model state from the agent-side entry without firing change-driven IPC calls.
    /// </summary>
    public void ApplyFromEntry(BalancerEntry entry, IReadOnlyList<RoutingListChoice> routingOptions, IReadOnlyList<string> allConfigs)
    {
        _suppress = true;
        try
        {
            Name = entry.Name;
            Mode = entry.Mode;
            RecheckSeconds = entry.RecheckSeconds > 0 ? entry.RecheckSeconds : 60;
            Status = entry.Status;
            ActiveMember = entry.ActiveMember;

            Members.Clear();
            foreach (var member in entry.Members)
            {
                Members.Add(member);
            }

            // Capture the "+ Новый список" intent BEFORE touching the options: clearing the bound
            // collection makes the ComboBox null its SelectedItem (writing null into SelectedRoutingList).
            var wasCreatingNew = SelectedRoutingList is { IsNewSentinel: true };

            // Rebuild the options only when the catalogue actually changed. A Clear()+Add() on every
            // snapshot would null the ComboBox's selection (which then NREs the read below and churns the
            // inline editor) even when nothing changed — e.g. a snapshot that only added a config.
            if (!RoutingListOptions.SequenceEqual(routingOptions))
            {
                RoutingListOptions.Clear();
                foreach (var option in routingOptions)
                {
                    RoutingListOptions.Add(option);
                }
            }

            var resolved = RoutingListOptions.FirstOrDefault(option => option.Id == entry.RoutingListId) ?? RoutingListChoice.None;
            // Keep an in-progress "+ Новый список" pick until the new list is saved+assigned (resolved
            // turns real); otherwise reflect the agent's stored list. Always assigns a non-null value.
            SelectedRoutingList = wasCreatingNew && !resolved.IsReal ? RoutingListChoice.NewList : resolved;
            UseRouting = entry.UseRouting && SelectedRoutingList.IsReal;
            RefreshAvailableConfigs(allConfigs);
        }
        finally
        {
            _suppress = false;
        }

        // Release the optimistic running override once the real status agrees with the user's last tap
        // (e.g. the agent has started connecting this profile, or finished disconnecting it).
        var statusRunning = !string.Equals(Status, ConnectionStatus.Disconnected, StringComparison.Ordinal);
        if (_pendingRunning == statusRunning)
        {
            _pendingRunning = null;
        }

        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(IsRunning));
    }

    /// <summary>
    /// Refreshes the "available configs" list (configs not yet members of this profile).
    /// </summary>
    public void RefreshAvailableConfigs(IReadOnlyList<string> allConfigs)
    {
        AvailableConfigs.Clear();
        AvailableConfigs.Add(NewConfigOption);
        foreach (var name in allConfigs)
        {
            if (!Members.Contains(name))
            {
                AvailableConfigs.Add(name);
            }
        }
    }

    // Make a member the default (the config used by default): move it to the front of the list. The
    // agent pins to the first member when not balancing, and treats it as highest priority when it is.
    [RelayCommand]
    private Task SetDefaultMember(string member)
    {
        var index = Members.IndexOf(member);
        if (index <= 0)
        {
            return Task.CompletedTask;
        }

        Members.Move(index, 0);
        return PersistMembersAsync();
    }

    /// <summary>
    /// Deletes a config from the catalogue entirely (not just this profile). The agent refuses while the
    /// config is in use by the running profile, so on a non-OK ack nothing changes; otherwise it is also
    /// dropped from this profile's members (the deleted name must not linger as a dangling member).
    /// </summary>
    public async Task DeleteConfigAsync(string member)
    {
        var ack = await _removeConfig(member);
        if (!ack.Ok)
        {
            return;
        }

        Members.Remove(member);
        AvailableConfigs.Remove(member);
        await PersistMembersAsync();
    }

    [RelayCommand]
    private Task MoveUp(string member)
    {
        var index = Members.IndexOf(member);
        if (index <= 0)
        {
            return Task.CompletedTask;
        }

        Members.Move(index, index - 1);
        return PersistMembersAsync();
    }

    [RelayCommand]
    private Task MoveDown(string member)
    {
        var index = Members.IndexOf(member);
        if (index < 0 || index >= Members.Count - 1)
        {
            return Task.CompletedTask;
        }

        Members.Move(index, index + 1);
        return PersistMembersAsync();
    }

    [RelayCommand]
    private async Task RemoveMember(string member)
    {
        if (!Members.Remove(member))
        {
            return;
        }

        // A profile may now be empty (configs are added back into it later).
        if (!AvailableConfigs.Contains(member))
        {
            AvailableConfigs.Add(member);
        }

        await PersistMembersAsync();
    }

    [RelayCommand]
    private async Task AddMember()
    {
        if (!CanAddMember || Members.Contains(AddMemberSelection!))
        {
            return;
        }

        var added = AddMemberSelection!;
        Members.Add(added);
        AvailableConfigs.Remove(added);
        AddMemberSelection = null;
        await PersistMembersAsync();
    }

    partial void OnAddMemberSelectionChanged(string? value)
    {
        // Ignore the combo reset during a snapshot apply, so the open inline form and the user's input
        // survive the periodic snapshot reconcile.
        if (_suppress)
        {
            return;
        }

        // Selecting the sentinel reveals the inline new-config form; a real config just enables "Добавить".
        IsCreatingConfig = value == NewConfigOption;
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

        var name = !string.IsNullOrWhiteSpace(NewConfigName) ? NewConfigName.Trim()
            : !string.IsNullOrWhiteSpace(imported.Name) ? imported.Name!.Trim()
            : "config";

        var ack = await _importConfig(name, imported.ConfText);
        if (!ack.Ok)
        {
            NewConfigStatus = ack.Message;
            return;
        }

        if (!Members.Contains(name))
        {
            Members.Add(name);
        }

        AvailableConfigs.Remove(name);
        await PersistMembersAsync();
        ResetNewConfig();
    }

    [RelayCommand]
    private void CancelNewConfig()
    {
        ResetNewConfig();
    }

    private void ResetNewConfig()
    {
        IsCreatingConfig = false;
        NewConfigName = string.Empty;
        NewConfigText = string.Empty;
        NewConfigStatus = string.Empty;
        AddMemberSelection = null;
    }

    partial void OnModeChanged(string value)
    {
        if (_suppress)
        {
            return;
        }

        _ = PersistMembersAsync();
    }

    partial void OnRecheckSecondsChanged(int value)
    {
        if (_suppress || value <= 0)
        {
            return;
        }

        _ = PersistMembersAsync();
    }

    partial void OnSelectedRoutingListChanged(RoutingListChoice value)
    {
        // value can momentarily be null: clearing the bound options nulls the ComboBox's SelectedItem.
        if (_suppress || value is null)
        {
            return;
        }

        // The "+ Новый список" sentinel has no id yet: the window builds the inline new-list editor and
        // binds the resulting list to this profile once it is first saved — nothing to assign here.
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

    private Task PersistMembersAsync()
    {
        if (_suppress)
        {
            return Task.CompletedTask;
        }

        // 0 members is allowed: a profile can be emptied and refilled later.
        return _saveBalancer(Name, RecheckSeconds, Mode, [.. Members]);
    }
}
