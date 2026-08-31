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
    GeoHttp geoHttp,
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
        geoHttp,
        fleet,
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

        // The order of the set is the order of the cards, and the priority the fallback walks.
        var library = snapshot.Configs.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var described = fleet.Describe([.. snapshot.Configs.Select(entry => entry.Name)]);
        var configs = described.Servers.Select(server => Card(library[server.Name], states)).ToList();
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
            Fleet = described,
        };
    }

    // How long a role change is given to stand the set back up, and how often that is looked at.
    private static readonly TimeSpan SettleWait = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan SettleStep = TimeSpan.FromMilliseconds(400);

    /// <inheritdoc/>
    protected override async Task<(string Config, bool Applied)> InspectTargetAsync(CancellationToken ct)
    {
        if (!mode.MultiServer)
        {
            return await base.InspectTargetAsync(ct);
        }

        // Every server runs a tunnel of its own, so a question about the tunnel is about the picked server.
        var name = control.Target ?? await CurrentScope.Store.GetSettingAsync(AgentControl.SelectedTargetKey, ct) ?? string.Empty;
        return (name, live.Of(name) is { Running: true });
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
            FleetOps.SetTarget => await TargetAsync(command.Args, ct),
            _ => await base.UnknownAsync(command, ct),
        };
    }

    /// <inheritdoc/>
    protected override Task ForgetConfigAsync(string name, CancellationToken ct)
    {
        if (!mode.MultiServer)
        {
            return base.ForgetConfigAsync(name, ct);
        }

        if (fleet.Forget(name))
        {
            log.LogInformation("'{Name}' was removed, so the set no longer lists it; the machine is asked for {Count} tunnel(s)", name, fleet.Wanted.Count);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task RetargetConfigAsync(string oldName, string newName, CancellationToken ct)
    {
        if (!mode.MultiServer)
        {
            return base.RetargetConfigAsync(oldName, newName, ct);
        }

        if (fleet.Rename(oldName, newName))
        {
            log.LogInformation("'{Old}' is called '{New}' from now on, and the set lists it under that name", oldName, newName);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override IReadOnlyCollection<string> RunningMembers()
    {
        if (!mode.MultiServer)
        {
            return base.RunningMembers();
        }

        return [.. fleet.Wanted.Where(name => live.Of(name) is { Running: true })];
    }

    /// <inheritdoc/>
    protected override TunnelDutyRoster Roster => mode.MultiServer ? fleet : base.Roster;

    /// <inheritdoc/>
    protected override void MarkRestartRequired(string config)
    {
        if (!mode.MultiServer)
        {
            base.MarkRestartRequired(config);
            return;
        }

        live.Of(config)?.SetRestartRequired();
    }

    /// <inheritdoc/>
    protected override void TakeRefreshed(SubscriptionOutcome outcome)
    {
        if (!mode.MultiServer)
        {
            base.TakeRefreshed(outcome);
            return;
        }

        // Снесённую конфигурацию набор роняет сам, переписанный текст встаёт на следующем подъёме туннеля.
        foreach (var name in outcome.Rewritten.Where(IsRunningMember))
        {
            MarkRestartRequired(name);
        }
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

        var turn = live.Turn;
        if (fleet.SetRole(name, role))
        {
            log.LogInformation("'{Name}' is the {Role} server from now on", name, TunnelRoles.Of(role));
            await SettledAsync(turn, ct);
        }

        return new IpcAck(true, $"{name}: {TunnelRoles.Of(role)}");
    }

    // Says where one rule of a routing list rides. Both ends left to the machine clear the address, so the
    // same request takes it back off the rule.
    private async Task<IpcAck> TargetAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count < 2 || !long.TryParse(args[0], out var listId) || args[1].Trim().Length == 0)
        {
            return new IpcAck(false, "fleet-set-target requires a list, a rule and where it rides");
        }

        var route = new RuleRoute(
            RuleTarget.Parse(args.Count > 2 ? args[2] : string.Empty),
            RuleTarget.Parse(args.Count > 3 ? args[3] : string.Empty));
        if (!route.IsDefault && RuleAddressing.ByName(args[1]))
        {
            log.LogInformation("rule '{Rule}' of list {List} is matched by name: the tunnel holding this machine's lookups hands the name to {Server}, and its addresses go through that one", args[1], listId, route.Target.Name.Length > 0 ? route.Target.Name : route.Fallback.Name);
        }

        foreach (var end in new[] { route.Target, route.Fallback })
        {
            if (end.Mode == RuleTarget.Server && !await CurrentScope.ConfigRepo.ExistsAsync(end.Name, ct))
            {
                return new IpcAck(false, $"unknown config: {end.Name}");
            }
        }

        var turn = live.Turn;
        if (fleet.SetTarget(FleetTargets.Key(listId, args[1]), route))
        {
            log.LogInformation("rule '{Rule}' of list {List} rides {Target}, and {Fallback} while that one is not up",
                args[1], listId, route.Target.Format(), route.Fallback.Format());
            await SettledAsync(turn, ct);
        }

        return new IpcAck(true, $"{args[1]}: {route.Format()}");
    }

    // A tunnel reads its duties at bring-up, so a role takes the ones it touches down and back up. The answer
    // waits for the set to stand again: whoever asked measures through the server it just named.
    private async Task SettledAsync(long turn, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + SettleWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(SettleStep, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // The round the request set off has to be over first: until then the set stands as it stood. A
            // tunnel counts once its server has answered, not once it was asked for.
            if (live.Turn != turn && fleet.Wanted.All(name => live.Of(name) is { Connected: true }))
            {
                return;
            }
        }
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
