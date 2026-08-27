using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// Answers the window while the machine keeps several tunnels up: the set rides the snapshot, and the requests
/// that move it are the mode's own. With the flag off it is the broker below it, to the line.
/// </summary>
internal sealed class FleetStatusBroker(
    GeoFileUpdater geoFileUpdater,
    GeoUpdateChecker geoUpdateChecker,
    AgentControl control,
    SettingsStore settingsStore,
    UpdateChecker updateChecker,
    UpdateState updateState,
    RouteManager routes,
    LogLevelController logLevel,
    DiagnosticsCollector diagnostics,
    SqliteLogStore logStore,
    ScopedStoreFactory storeFactory,
    IGeoFileStore geoFiles,
    ServiceManager serviceManager,
    UserStoreRegistry registry,
    ActiveTunnelScope activeScope,
    RuntimeInspector inspector,
    CheckService checks,
    LocalProxyService proxy,
    WindowsHotspotService hotspot,
    ILogger<AgentStatusBroker> logger,
    AgentMode mode,
    FleetControl fleet,
    FleetLive live,
    ActiveTunnelScope owner,
    ILogger<FleetStatusBroker> log) : AgentStatusBroker(
        geoFileUpdater,
        geoUpdateChecker,
        control,
        settingsStore,
        updateChecker,
        updateState,
        routes,
        logLevel,
        diagnostics,
        logStore,
        storeFactory,
        geoFiles,
        serviceManager,
        registry,
        activeScope,
        inspector,
        checks,
        proxy,
        hotspot,
        logger)
{
    /// <inheritdoc/>
    protected override StatusSnapshot Describe(StatusSnapshot snapshot, BrokerScope scope, IReadOnlyList<TunnelState> states)
    {
        if (!mode.MultiServer)
        {
            return snapshot;
        }

        // The set belongs to the user whose tunnels these are; another user sees their own idle library.
        if (!owner.IsOwnedBy(scope.UserRoot, scope.Sid))
        {
            return snapshot;
        }

        var configs = snapshot.Configs.Select(entry => Card(entry, states)).ToList();
        var selected = snapshot.SelectedTarget ?? string.Empty;
        var standing = selected.Length > 0 ? live.Of(selected) : null;

        // The header answers for the selected server alone: the machine may hold several, and the rest of the
        // set is neither connected nor disconnected by it.
        return snapshot with
        {
            Configs = configs,
            BoundTarget = selected.Length > 0 ? selected : null,
            BoundStatus = Status(selected, states) ?? ConnectionStatus.Disconnected,
            Active = fleet.Wanted.Contains(selected),
            RestartRequired = standing?.RestartRequired ?? false,
            ConnectFailed = standing?.ConnectFailed ?? false,
            ConnectFailReason = standing?.ConnectFailed == true ? standing.ConnectFailReason.ToString() : string.Empty,
            ConnectFailDetail = standing?.ConnectFailed == true ? (standing.ConnectFailDetail ?? string.Empty) : string.Empty,
            DisconnectFailed = standing?.DisconnectFailed ?? false,
            DisconnectFailDetail = standing?.DisconnectFailed == true ? (standing.DisconnectFailDetail ?? string.Empty) : string.Empty,
            RetryAttempt = standing?.RetryAttempt ?? 0,
            Fleet = fleet.Describe([.. configs.Select(entry => entry.Name)]),
        };
    }

    /// <inheritdoc/>
    protected override async Task<IpcAck> UnknownAsync(IpcCommand command, CancellationToken ct)
    {
        if (!mode.MultiServer)
        {
            return await base.UnknownAsync(command, ct);
        }

        return command.Op switch
        {
            FleetOps.Connect => await ConnectAsync(Named(command.Args), command.Args, ct),
            FleetOps.Disconnect => Disconnect(Named(command.Args)),
            FleetOps.SetPrimary => await RoleAsync(Named(command.Args), TunnelRoles.Primary, ct),
            FleetOps.SetRole => await RoleAsync(Named(command.Args), command.Args.Count > 1 ? command.Args[1] : string.Empty, ct),
            FleetOps.Reorder => await ReorderAsync(command.Args, ct),
            _ => await base.UnknownAsync(command, ct),
        };
    }

    /// <inheritdoc/>
    protected override async Task<IpcAck> SetConnectionAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!mode.MultiServer)
        {
            return await base.SetConnectionAsync(args, ct);
        }

        if (args.Count < 1)
        {
            return new IpcAck(false, "set-connection requires connect or disconnect");
        }

        var connect = args[0].Equals("connect", StringComparison.OrdinalIgnoreCase);
        if (!connect && !args[0].Equals("disconnect", StringComparison.OrdinalIgnoreCase))
        {
            return new IpcAck(false, $"unknown connection state: {args[0]}");
        }

        // The header connects the server it shows, and in the mode that joins it to the set instead of taking
        // the machine off whatever else it stands on.
        var selected = await CurrentScope.Store.GetSettingAsync(AgentControl.SelectedTargetKey, ct) ?? string.Empty;
        if (selected.Length == 0)
        {
            return new IpcAck(false, "no configuration is selected");
        }

        return connect ? await ConnectAsync(selected, args, ct) : Disconnect(selected);
    }

    // Asks for one server. The set belongs to whoever owns the machine's tunnels, so the first request takes it.
    private async Task<IpcAck> ConnectAsync(string name, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (name.Length == 0)
        {
            return new IpcAck(false, "a server has to be named");
        }

        var scope = CurrentScope;
        if (!await scope.ConfigRepo.ExistsAsync(name, ct))
        {
            return new IpcAck(false, $"unknown config: {name}");
        }

        if (fleet.Wanted.Count > 0
            && !owner.IsOwnedBy(scope.UserRoot, scope.Sid)
            && !args.Any(arg => arg.Equals("takeover", StringComparison.OrdinalIgnoreCase)))
        {
            return new IpcAck(false, IpcMessage.Key("Agent_TunnelOwnedByOther"));
        }

        owner.SetOwner(scope.UserRoot, scope.Sid);
        await scope.Store.SetSettingAsync("last-owner-root", scope.UserRoot, ct);
        if (!fleet.Add(name))
        {
            return new IpcAck(true, $"already asked for: {name}");
        }

        log.LogInformation("'{Name}' was asked for by {Root}; the machine is now asked for {Count} tunnel(s)", name, scope.UserRoot, fleet.Wanted.Count);
        return new IpcAck(true, "connecting");
    }

    // Takes one server out of the set; the rest stand.
    private IpcAck Disconnect(string name)
    {
        if (name.Length == 0)
        {
            return new IpcAck(false, "a server has to be named");
        }

        if (!fleet.Remove(name))
        {
            return new IpcAck(true, $"not asked for: {name}");
        }

        log.LogInformation("'{Name}' is no longer asked for; the machine is asked for {Count} tunnel(s)", name, fleet.Wanted.Count);
        return new IpcAck(true, "disconnecting");
    }

    private async Task<IpcAck> RoleAsync(string name, string role, CancellationToken ct)
    {
        if (name.Length == 0)
        {
            return new IpcAck(false, "a server has to be named");
        }

        if (!TunnelRoles.IsKnown(role))
        {
            return new IpcAck(false, $"unknown role: {role}");
        }

        if (!await CurrentScope.ConfigRepo.ExistsAsync(name, ct))
        {
            return new IpcAck(false, $"unknown config: {name}");
        }

        fleet.SetRole(name, role);
        log.LogInformation("'{Name}' is the {Role} server from now on", name, TunnelRoles.Of(role));
        return new IpcAck(true, $"{name}: {TunnelRoles.Of(role)}");
    }

    // The order the servers are listed in is the order the mode falls back through.
    private async Task<IpcAck> ReorderAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        if (names.Count == 0)
        {
            return new IpcAck(false, "the order has to name the servers");
        }

        foreach (var name in names)
        {
            if (!await CurrentScope.ConfigRepo.ExistsAsync(name, ct))
            {
                return new IpcAck(false, $"unknown config: {name}");
            }
        }

        fleet.SetOrder([.. names]);
        return new IpcAck(true, "reordered");
    }

    // A card shows its own tunnel: the status its supervisor wrote down and the readings it takes. One the mode
    // does not hold shows nothing, whatever the machine stood on before the flag moved.
    private ConfigEntry Card(ConfigEntry entry, IReadOnlyList<TunnelState> states)
    {
        var standing = live.Of(entry.Name);
        if (standing is null)
        {
            return entry with
            {
                Status = ConnectionStatus.Idle,
                HandshakeAgeSeconds = -1,
                RxBitsPerSecond = 0,
                TxBitsPerSecond = 0,
                HandshakesPerMinute = 0,
                LossPercent = LinkHealth.LossUnknown,
                RttMs = -1,
            };
        }

        var link = standing.Link;
        return entry with
        {
            Status = DisplayStatus(Status(entry.Name, states) ?? ConnectionStatus.Connecting),
            HandshakeAgeSeconds = standing.HandshakeAge,
            RxBitsPerSecond = link.RxBitsPerSecond,
            TxBitsPerSecond = link.TxBitsPerSecond,
            HandshakesPerMinute = link.HandshakesPerMinute,
            LossPercent = link.LossPercent,
            RttMs = link.RttMs,
        };
    }

    // What a tunnel of the set last wrote down, or null while it is not up.
    private string? Status(string name, IReadOnlyList<TunnelState> states)
    {
        if (name.Length == 0 || live.Of(name) is null)
        {
            return null;
        }

        return states.FirstOrDefault(state => string.Equals(state.Name, name, StringComparison.Ordinal))?.Status;
    }

    private static string Named(IReadOnlyList<string> args)
    {
        return args.Count > 0 ? args[0].Trim() : string.Empty;
    }
}
