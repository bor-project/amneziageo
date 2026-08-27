using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Каталог, пока машина держит несколько туннелей: карточки режима, роли на них и порядок серверов.
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

    /// <inheritdoc/>
    protected override IpcCommand OrderCommand(IReadOnlyList<string> names)
    {
        return MultiServer ? new IpcCommand(FleetOps.Reorder, names) : base.OrderCommand(names);
    }

    /// <summary>
    /// Ставит серверу роль. «Основной» на машине один, и просят его своим запросом.
    /// </summary>
    internal async Task SetRoleAsync(string name, string role)
    {
        var primary = string.Equals(role, TunnelRoles.Primary, StringComparison.Ordinal);
        var ack = await _link.SendCommandAsync(primary
            ? new IpcCommand(FleetOps.SetPrimary, [name])
            : new IpcCommand(FleetOps.SetRole, [name, role]));
        if (!ack.Ok)
        {
            _shell.Home.ShowNotice(FleetNotice.Of(ack));
        }
    }

    // Раскладывает набор по карточкам: состояние своё у каждого сервера, роль - из набора.
    private void Describe(StatusSnapshot snapshot)
    {
        var servers = new Dictionary<string, FleetEntry>(StringComparer.Ordinal);
        foreach (var server in snapshot.Fleet?.Servers ?? [])
        {
            servers[server.Name] = server;
        }

        foreach (var entry in snapshot.Configs)
        {
            if (Row(entry.Name) is not { } card)
            {
                continue;
            }

            card.Status = entry.Status;
            card.Role = servers.TryGetValue(entry.Name, out var server) ? server.Role : TunnelRoles.Default;
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
