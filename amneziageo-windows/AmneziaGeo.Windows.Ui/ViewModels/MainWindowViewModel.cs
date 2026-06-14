using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// Top-level view model: agent status, the configuration list, and the balancer list.
/// </summary>
internal sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly AgentConnection _connection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    [NotifyPropertyChangedFor(nameof(AgentStatusBrush))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentStatusText))]
    private string? _boundTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasConfigs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasBalancers;

    /// <summary>
    /// ctor
    /// </summary>
    public MainWindowViewModel(AgentConnection connection)
    {
        _connection = connection;
        _connection.Connected += OnConnected;
        _connection.Disconnected += OnDisconnected;
        _connection.SnapshotReceived += OnSnapshot;
    }

    /// <summary>
    /// The connection used to talk to the agent.
    /// </summary>
    public AgentConnection Connection => _connection;

    /// <summary>
    /// The configuration rows.
    /// </summary>
    public ObservableCollection<ConfigItemViewModel> Configs { get; } = [];

    /// <summary>
    /// The balancer rows.
    /// </summary>
    public ObservableCollection<BalancerItemViewModel> Balancers { get; } = [];

    /// <summary>
    /// The agent status banner text.
    /// </summary>
    public string AgentStatusText
    {
        get
        {
            if (!IsConnected)
            {
                return "Агент не запущен";
            }

            return BoundTarget is null ? "Подключено к агенту" : $"Активное подключение: {BoundTarget}";
        }
    }

    /// <summary>
    /// The agent status indicator color.
    /// </summary>
    public IBrush AgentStatusBrush => StatusLabels.Brush(IsConnected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected);

    /// <summary>
    /// Whether nothing is configured yet.
    /// </summary>
    public bool IsEmpty => !HasConfigs && !HasBalancers;

    /// <summary>
    /// The hint shown when nothing is configured.
    /// </summary>
    public string EmptyHint => "Нет конфигураций. Нажмите «Добавить».";

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
        return [.. Configs.Select(config => config.Name)];
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
            Configs.Clear();
            Balancers.Clear();
            HasConfigs = false;
            HasBalancers = false;
        });
    }

    private void OnSnapshot(StatusSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => Apply(snapshot));
    }

    private void Apply(StatusSnapshot snapshot)
    {
        BoundTarget = snapshot.BoundTarget;
        SyncConfigs(snapshot.Configs);
        SyncBalancers(snapshot.Balancers);
        HasConfigs = Configs.Count > 0;
        HasBalancers = Balancers.Count > 0;
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
                Status = entry.Status,
            });
        }
    }

    private void SyncBalancers(IReadOnlyList<BalancerEntry> entries)
    {
        Balancers.Clear();
        foreach (var entry in entries)
        {
            Balancers.Add(new BalancerItemViewModel
            {
                Name = entry.Name,
                Detail = BalancerDetail(entry),
                Status = entry.Status,
            });
        }
    }

    private static string BalancerDetail(BalancerEntry entry)
    {
        var mode = entry.Mode == "latency" ? "по задержке" : "по приоритету";
        var active = entry.ActiveMember ?? "—";
        return $"{mode} · активен: {active} · серверов: {entry.Members.Count}";
    }
}
