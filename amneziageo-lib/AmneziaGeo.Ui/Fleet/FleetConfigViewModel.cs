using System.Collections.ObjectModel;
using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Каталог, пока машина держит несколько туннелей: карточки режима и место каждого сервера в цепочке.
/// </summary>
internal sealed class FleetConfigViewModel : ConfigViewModel
{
    private readonly IAgentConnection _link;
    private readonly MainWindowViewModel _shell;

    /// <summary>
    /// ctor
    /// </summary>
    public FleetConfigViewModel(MainWindowViewModel host, IAgentConnection connection)
        : base(host, connection)
    {
        _link = connection;
        _shell = host;
    }

    /// <summary>
    /// Держит ли машина несколько туннелей разом.
    /// </summary>
    public bool MultiServer { get; private set; }

    /// <summary>
    /// Сколько серверов стоит в цепочке.
    /// </summary>
    internal int ChainLength { get; private set; }

    /// <summary>
    /// Чем набор поднимется, когда его включат целиком; пусто, пока он стоит.
    /// </summary>
    internal IReadOnlyList<string> Resume { get; private set; } = [];

    /// <summary>
    /// Поднятые серверы набора: их показывает главный экран.
    /// </summary>
    public ObservableCollection<FleetConfigItemViewModel> Standing { get; } = [];

    /// <summary>
    /// Сколько серверов набора уже поднято: в списке стоят и те, которых ещё добиваются.
    /// </summary>
    public string StandingText => Loc.Instance.Get(
        "Main_HomeConnectedCount",
        Standing.Count(card => card.Status is ConnectionStatus.Connected).ToString(CultureInfo.InvariantCulture));

    // Сводит список с каталогом, не пересобирая его целиком: пересборка дёргала бы строки на экране. В списке
    // стоят заявленные серверы, а у снятого набора - те, которыми он поднимется, чтобы было видно, что вернётся.
    private void SyncStanding()
    {
        var standing = Configs
            .OfType<FleetConfigItemViewModel>()
            .Where(card => card.Wanted || Resume.Contains(card.Name, StringComparer.Ordinal))
            .ToList();
        for (var at = Standing.Count - 1; at >= 0; at--)
        {
            if (!standing.Contains(Standing[at]))
            {
                Standing.RemoveAt(at);
            }
        }

        for (var at = 0; at < standing.Count; at++)
        {
            if (!Standing.Contains(standing[at]))
            {
                Standing.Insert(Math.Min(at, Standing.Count), standing[at]);
            }
        }

        OnPropertyChanged(nameof(StandingText));
    }

    /// <inheritdoc/>
    public override void Apply(StatusSnapshot snapshot)
    {
        // Тип карточки задаёт режим, поэтому на его движении строки собираются заново.
        if (snapshot.MultiServer != MultiServer)
        {
            MultiServer = snapshot.MultiServer;
            Configs.Clear();
        }

        base.Apply(snapshot);
        if (MultiServer)
        {
            Describe(snapshot);
        }
    }

    /// <inheritdoc/>
    protected override ConfigItemViewModel NewRow(string name)
    {
        return MultiServer ? new FleetConfigItemViewModel(this) { Name = name } : base.NewRow(name);
    }

    /// <summary>
    /// Свободны ли места: замер держит машину, и до его конца их не двигают.
    /// </summary>
    internal bool RolesFree => _shell.HomeFleet?.RolesLocked != true;

    /// <summary>
    /// Пересчитывает запор мест на карточках.
    /// </summary>
    internal void NotifyRoleGate()
    {
        foreach (var row in Configs)
        {
            if (row is FleetConfigItemViewModel card)
            {
                card.RefreshRoleGate();
            }
        }
    }

    /// <summary>
    /// Ставит сервер на место в цепочке.
    /// </summary>
    internal async Task SetSlotAsync(string name, int slot)
    {
        var ack = await _link.SendCommandAsync(new IpcCommand(FleetOps.SetSlot,
            [name, slot.ToString(CultureInfo.InvariantCulture)]));
        if (!ack.Ok)
        {
            _shell.Home.ShowNotice(FleetNotice.Of(ack));
        }
    }

    // Раскладывает набор по карточкам: состояние своё у каждого сервера, место - из набора.
    private void Describe(StatusSnapshot snapshot)
    {
        var servers = new Dictionary<string, FleetEntry>(StringComparer.Ordinal);
        foreach (var server in snapshot.Fleet?.Servers ?? [])
        {
            servers[server.Name] = server;
        }

        ChainLength = servers.Values.Count(server => server.Slot > TunnelRoles.Aside);
        Resume = snapshot.Fleet?.Resume ?? [];
        foreach (var entry in snapshot.Configs)
        {
            if (Row(entry.Name) is not { } card)
            {
                continue;
            }

            if (card.Accepts(entry.Status))
            {
                card.Status = entry.Status;
            }

            var listed = servers.TryGetValue(entry.Name, out var server);
            card.Slot = listed ? server!.Slot : TunnelRoles.Aside;
            card.Wanted = listed && server!.Wanted;
        }

        SyncStanding();
    }

    // Карточка режима по имени сервера.
    private FleetConfigItemViewModel? Row(string name)
    {
        foreach (var row in Configs)
        {
            if (row is FleetConfigItemViewModel card && string.Equals(card.Name, name, StringComparison.Ordinal))
            {
                return card;
            }
        }

        return null;
    }
}
