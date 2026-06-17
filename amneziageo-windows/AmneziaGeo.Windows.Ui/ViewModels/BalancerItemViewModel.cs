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
    private bool _suppress;

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
    private string _status = ConnectionStatus.Disconnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string? _activeMember;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isActive;

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
        Func<string, string, Task<IpcAck>> importConfig)
    {
        _saveBalancer = saveBalancer;
        _assignRouting = assignRouting;
        _selectProfile = selectProfile;
        _importConfig = importConfig;

        // Adding/removing a member flips whether the balancer is available (>1 config), so refresh the
        // dependent state when the membership changes.
        Members.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanUseBalancer));
            OnPropertyChanged(nameof(IsOffMode));
            OnPropertyChanged(nameof(IsPriorityMode));
            OnPropertyChanged(nameof(IsLatencyMode));
            OnPropertyChanged(nameof(ShowInterval));
            OnPropertyChanged(nameof(Detail));
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
    /// True when a real routing list is selected and the toggle can flip use_routing on.
    /// </summary>
    public bool CanToggleRouting => !SelectedRoutingList.IsNone;

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

            RoutingListOptions.Clear();
            foreach (var option in routingOptions)
            {
                RoutingListOptions.Add(option);
            }

            SelectedRoutingList = RoutingListOptions.FirstOrDefault(option => option.Id == entry.RoutingListId) ?? RoutingListChoice.None;
            UseRouting = entry.UseRouting && !SelectedRoutingList.IsNone;
            RefreshAvailableConfigs(allConfigs);
        }
        finally
        {
            _suppress = false;
        }

        OnPropertyChanged(nameof(Detail));
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
        if (_suppress)
        {
            return;
        }

        if (value.IsNone && UseRouting)
        {
            UseRouting = false;
        }

        _ = _assignRouting(Name, value.Id, UseRouting && !value.IsNone);
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
