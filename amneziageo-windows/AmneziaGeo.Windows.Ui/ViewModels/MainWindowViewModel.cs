using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Top-level view model: connection card, nav state, profile list, routing-list catalogue, and theme.
/// </summary>
internal sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;
    private readonly DispatcherTimer _noticeTimer;
    private IReadOnlyList<string> _configNames = [];
    private bool _toggleInFlight;
    private string? _lastNotice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    [NotifyPropertyChangedFor(nameof(AgentStatusBrush))]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    [NotifyPropertyChangedFor(nameof(ConnectButtonBrush))]
    private bool _isTunnelActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    [NotifyPropertyChangedFor(nameof(ActiveProfileName))]
    private string? _boundTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    [NotifyPropertyChangedFor(nameof(AgentStatusBrush))]
    private string _boundStatus = ConnectionStatus.Disconnected;

    [ObservableProperty]
    private string? _activeMember;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasConfigs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasBalancers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasRoutingLists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome))]
    [NotifyPropertyChangedFor(nameof(IsRouting))]
    [NotifyPropertyChangedFor(nameof(IsSettings))]
    private string _nav = "home";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeLabel))]
    private bool _isDark;

    [ObservableProperty]
    private bool _noticeVisible;

    [ObservableProperty]
    private string? _noticeText;

    [ObservableProperty]
    private bool _killSwitchEnabled;

    [ObservableProperty]
    private bool _allowLan = true;

    // Set while applying a snapshot so echoing the agent's current settings into the toggles does not
    // bounce straight back as a set-setting command.
    private bool _suppressSettingPush;

    /// <summary>
    /// ctor
    /// </summary>
    public MainWindowViewModel(AgentConnection connection)
    {
        _connection = connection;
        _connection.Connected += OnConnected;
        _connection.Disconnected += OnDisconnected;
        _connection.SnapshotReceived += OnSnapshot;
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            NoticeVisible = false;
        };
    }

    /// <summary>
    /// The connection used to talk to the agent.
    /// </summary>
    public AgentConnection Connection => _connection;

    /// <summary>
    /// Configuration rows.
    /// </summary>
    public ObservableCollection<ConfigItemViewModel> Configs { get; } = [];

    /// <summary>
    /// Profile rows.
    /// </summary>
    public ObservableCollection<BalancerItemViewModel> Balancers { get; } = [];

    /// <summary>
    /// Routing-list catalogue.
    /// </summary>
    public ObservableCollection<RoutingListSummaryViewModel> RoutingLists { get; } = [];

    /// <summary>
    /// Banner status text in the connection card.
    /// </summary>
    public string AgentStatusText => IsConnected ? StatusLabels.Text(BoundStatus) : "Нет связи с агентом";

    /// <summary>
    /// Connection-card status indicator color.
    /// </summary>
    public IBrush AgentStatusBrush => StatusLabels.Brush(IsConnected ? BoundStatus : ConnectionStatus.Disconnected);

    /// <summary>
    /// Name shown under the status banner in the connection card.
    /// </summary>
    public string ActiveProfileName => BoundTarget ?? "—";

    /// <summary>
    /// Whether nothing is configured yet.
    /// </summary>
    public bool IsEmpty => !HasConfigs && !HasBalancers && !HasRoutingLists;

    /// <summary>
    /// The hint shown when nothing is configured.
    /// </summary>
    public string EmptyHint => "Нет конфигураций. Нажмите «+ Профиль» или «Добавить».";

    /// <summary>
    /// Big connect / disconnect button label, reflecting the agent's desired tunnel state.
    /// </summary>
    public string ConnectButtonText => IsTunnelActive ? "Остановить" : "Запустить";

    /// <summary>
    /// Big connect / disconnect button color.
    /// </summary>
    public IBrush ConnectButtonBrush => StatusLabels.Brush(IsTunnelActive ? ConnectionStatus.Disconnected : ConnectionStatus.Connected);

    /// <summary>
    /// Whether the connect / disconnect button is actionable (the agent pipe is up).
    /// </summary>
    public bool CanToggleConnection => IsConnected;

    /// <summary>
    /// Whether the Home tab is shown.
    /// </summary>
    public bool IsHome => Nav == "home";

    /// <summary>
    /// Whether the Routing tab is shown.
    /// </summary>
    public bool IsRouting => Nav == "routing";

    /// <summary>
    /// Whether the Settings tab is shown.
    /// </summary>
    public bool IsSettings => Nav == "settings";

    /// <summary>
    /// Current theme label shown on the toggle button.
    /// </summary>
    public string ThemeLabel => IsDark ? "Тёмная" : "Светлая";

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
        return _configNames;
    }

    [RelayCommand]
    private void NavHome()
    {
        Nav = "home";
    }

    [RelayCommand]
    private void NavRouting()
    {
        Nav = "routing";
    }

    [RelayCommand]
    private void NavSettings()
    {
        Nav = "settings";
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDark = !IsDark;
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleConnection))]
    private async Task ToggleConnection()
    {
        var connect = !IsTunnelActive;
        IsTunnelActive = connect;
        BoundStatus = connect ? ConnectionStatus.Connecting : ConnectionStatus.Disconnecting;
        _toggleInFlight = true;
        try
        {
            var ack = await _connection.SendCommandAsync(
                new IpcCommand(IpcContract.OpSetConnection, [connect ? "connect" : "disconnect"]));
            if (!ack.Ok)
            {
                IsTunnelActive = !connect;
            }
        }
        finally
        {
            _toggleInFlight = false;
        }
    }

    private void OnConnected()
    {
        Dispatcher.UIThread.Post(() => IsConnected = true);
    }

    private void OnDisconnected()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            BoundStatus = ConnectionStatus.Disconnected;
            Configs.Clear();
            Balancers.Clear();
            RoutingLists.Clear();
            HasConfigs = false;
            HasBalancers = false;
            HasRoutingLists = false;
            _configNames = [];
            ActiveMember = null;
            _noticeTimer.Stop();
            _lastNotice = null;
            NoticeVisible = false;
            NoticeText = null;
        });
    }

    private void OnSnapshot(StatusSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => Apply(snapshot));
    }

    private void Apply(StatusSnapshot snapshot)
    {
        BoundTarget = snapshot.BoundTarget;
        BoundStatus = snapshot.BoundStatus;
        if (!_toggleInFlight)
        {
            IsTunnelActive = snapshot.Active;
        }

        SyncConfigs(snapshot.Configs);
        SyncRoutingLists(snapshot.RoutingLists ?? []);
        SyncBalancers(snapshot.Balancers, snapshot.RoutingLists ?? []);
        HasConfigs = Configs.Count > 0;
        HasBalancers = Balancers.Count > 0;
        HasRoutingLists = RoutingLists.Count > 0;

        var bound = snapshot.Balancers.FirstOrDefault(b => b.Name == snapshot.BoundTarget);
        ActiveMember = bound?.ActiveMember;

        foreach (var item in Balancers)
        {
            item.IsActive = string.Equals(item.Name, snapshot.SelectedTarget ?? snapshot.BoundTarget, StringComparison.Ordinal);
        }

        // Top-center notice (auto-hides after 5s, dismissable): a different profile is selected while a
        // tunnel is up (reconnect to apply — no auto-switch), settings changed on a live tunnel, or a
        // better member is available on a backup. Shown once per distinct notice, not re-armed while
        // the same one holds.
        string? notice = null;
        if (snapshot.Active && snapshot.SelectedTarget is not null
            && !string.Equals(snapshot.SelectedTarget, snapshot.BoundTarget, StringComparison.Ordinal))
        {
            notice = $"Выбран профиль «{snapshot.SelectedTarget}». Переподключитесь, чтобы применить.";
        }
        else if (snapshot.RestartRequired)
        {
            notice = "Настройки изменены. Переподключитесь, чтобы применить.";
        }
        else if (snapshot.BetterMember is not null)
        {
            notice = $"Доступно приоритетное подключение: {snapshot.BetterMember}. Переподключитесь, чтобы вернуться.";
        }

        ShowNotice(notice);

        _suppressSettingPush = true;
        KillSwitchEnabled = snapshot.KillSwitchEnabled;
        AllowLan = snapshot.AllowLan;
        _suppressSettingPush = false;
    }

    /// <summary>
    /// Shows a transient notice banner that auto-hides after 5 seconds. Re-arms only when the notice
    /// text changes, so a persistent condition is not re-shown on every snapshot (and a dismissed
    /// banner stays dismissed until a different notice arrives).
    /// </summary>
    private void ShowNotice(string? notice)
    {
        if (string.Equals(notice, _lastNotice, StringComparison.Ordinal))
        {
            return;
        }

        _lastNotice = notice;
        NoticeText = notice;
        _noticeTimer.Stop();
        if (notice is not null)
        {
            NoticeVisible = true;
            _noticeTimer.Start();
        }
        else
        {
            NoticeVisible = false;
        }
    }

    [RelayCommand]
    private void DismissNotice()
    {
        _noticeTimer.Stop();
        NoticeVisible = false;
    }

    partial void OnKillSwitchEnabledChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync("killswitch", value);
        }
    }

    partial void OnAllowLanChanged(bool value)
    {
        if (!_suppressSettingPush)
        {
            _ = SetSettingAsync("allow-lan", value);
        }
    }

    private async Task SetSettingAsync(string key, bool value)
    {
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSetSetting, [key, value ? "on" : "off"]));
    }

    private void SyncConfigs(IReadOnlyList<ConfigEntry> entries)
    {
        Configs.Clear();
        foreach (var entry in entries)
        {
            Configs.Add(new ConfigItemViewModel
            {
                Name = entry.Name,
                Endpoint = entry.Endpoint,
                GeoSplit = entry.GeoSplit,
                Rules = entry.Rules,
                Status = entry.Status,
            });
        }

        _configNames = [.. entries.Select(e => e.Name)];
    }

    private void SyncRoutingLists(IReadOnlyList<RoutingListEntry> entries)
    {
        RoutingLists.Clear();
        foreach (var entry in entries)
        {
            RoutingLists.Add(new RoutingListSummaryViewModel
            {
                Id = entry.Id,
                Name = entry.Name,
                RuleCount = entry.RuleCount,
                RouteCount = entry.RouteCount,
                DomainCount = entry.DomainCount,
            });
        }
    }

    private void SyncBalancers(IReadOnlyList<BalancerEntry> entries, IReadOnlyList<RoutingListEntry> routingLists)
    {
        var options = BuildRoutingOptions(routingLists);

        // Reconcile in place, matching rows by name, so transient view state (the expanded
        // editor, combo selection) survives the snapshot pushes that follow every edit.
        var present = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = Balancers.Count - 1; i >= 0; i--)
        {
            if (!present.Contains(Balancers[i].Name))
            {
                Balancers.RemoveAt(i);
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var existing = Balancers.FirstOrDefault(b => string.Equals(b.Name, entry.Name, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new BalancerItemViewModel(SaveBalancerAsync, AssignRoutingAsync, SelectProfileAsync);
                existing.ApplyFromEntry(entry, options, _configNames);
                Balancers.Insert(Math.Min(i, Balancers.Count), existing);
                continue;
            }

            existing.ApplyFromEntry(entry, options, _configNames);
            var index = Balancers.IndexOf(existing);
            if (index != i)
            {
                Balancers.Move(index, i);
            }
        }
    }

    private static IReadOnlyList<RoutingListChoice> BuildRoutingOptions(IReadOnlyList<RoutingListEntry> entries)
    {
        var options = new List<RoutingListChoice> { RoutingListChoice.None };
        foreach (var entry in entries)
        {
            options.Add(new RoutingListChoice(entry.Id, entry.Name));
        }

        return options;
    }

    private async Task SaveBalancerAsync(string name, int recheck, string mode, IReadOnlyList<string> members)
    {
        var args = new List<string> { name, recheck.ToString(System.Globalization.CultureInfo.InvariantCulture), mode };
        args.AddRange(members);
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpAddBalancer, args));
    }

    private async Task AssignRoutingAsync(string profile, long? listId, bool useRouting)
    {
        var args = new[]
        {
            profile,
            listId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
            useRouting ? "on" : "off",
        };
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpAssignRouting, args));
    }

    private async Task SelectProfileAsync(string profile)
    {
        await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpSelectProfile, [profile]));
    }
}
