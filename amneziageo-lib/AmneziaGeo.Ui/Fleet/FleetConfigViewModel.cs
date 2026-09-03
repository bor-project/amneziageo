using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
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
        foreach (var entry in snapshot.Configs)
        {
            if (Row(entry.Name) is not { } card)
            {
                continue;
            }

            if (!card.Dialing)
            {
                card.Status = entry.Status;
            }

            card.Slot = servers.TryGetValue(entry.Name, out var server) ? server.Slot : TunnelRoles.Aside;
        }
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
