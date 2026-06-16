using System.Collections.ObjectModel;
using Avalonia.Media;
using AmneziaGeo.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// A single profile (balancer group) row in the list, with its expanded editor state.
/// Mutations are dispatched as IPC commands to the agent via the provided action delegates.
/// </summary>
internal sealed partial class BalancerItemViewModel : ViewModelBase
{
    private readonly Func<string, int, string, IReadOnlyList<string>, Task> _saveBalancer;
    private readonly Func<string, long?, bool, Task> _assignRouting;
    private readonly Func<string, Task> _selectProfile;
    private bool _suppress;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(IsPriorityMode))]
    [NotifyPropertyChangedFor(nameof(IsLatencyMode))]
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
    private RoutingListChoice _selectedRoutingList = RoutingListChoice.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleRouting))]
    private bool _useRouting;

    [ObservableProperty]
    private string? _addMemberSelection;

    /// <summary>
    /// ctor
    /// </summary>
    public BalancerItemViewModel(
        Func<string, int, string, IReadOnlyList<string>, Task> saveBalancer,
        Func<string, long?, bool, Task> assignRouting,
        Func<string, Task> selectProfile)
    {
        _saveBalancer = saveBalancer;
        _assignRouting = assignRouting;
        _selectProfile = selectProfile;
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
    /// Whether the profile uses the priority-order balancer mode.
    /// </summary>
    public bool IsPriorityMode => Mode == "priority";

    /// <summary>
    /// Whether the profile uses the best-availability balancer mode.
    /// </summary>
    public bool IsLatencyMode => Mode == "latency";

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
            var mode = Mode == "latency" ? "по задержке" : "по приоритету";
            var active = ActiveMember ?? "—";
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

        if (Members.Count == 0)
        {
            // A profile must keep at least one member.
            Members.Add(member);
            return;
        }

        if (!AvailableConfigs.Contains(member))
        {
            AvailableConfigs.Add(member);
        }

        await PersistMembersAsync();
    }

    [RelayCommand]
    private async Task AddMember()
    {
        if (AddMemberSelection is null || Members.Contains(AddMemberSelection))
        {
            return;
        }

        var added = AddMemberSelection;
        Members.Add(added);
        AvailableConfigs.Remove(added);
        AddMemberSelection = null;
        await PersistMembersAsync();
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
        if (_suppress || Members.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _saveBalancer(Name, RecheckSeconds, Mode, [.. Members]);
    }
}
