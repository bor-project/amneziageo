using System.Net;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Engine;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Dispatches console subcommands to the application services.
/// </summary>
internal sealed class Cli(
    IStateStore store,
    ServiceManager serviceManager,
    ConfigRepository configRepo,
    SettingsStore settingsStore,
    GeoFileUpdater geoFileUpdater,
    GeoActivator geoActivator,
    RouteManager routes,
    UapiClient uapi,
    NetworkReconciler reconciler,
    TunnelRunner tunnelRunner,
    BalancerRunner balancerRunner,
    BackupService backupService,
    GeoConfigurator geoConfigurator)
{
    /// <summary>
    /// Runs the subcommand matching the given arguments.
    /// </summary>
    public async Task<int> RunAsync(string[] args)
    {
        switch (args)
        {
            case ["--service", var name]:
                await tunnelRunner.RunAsync(name);
                return 0;
            case ["install", var name, var configPath]:
                return serviceManager.Install(name, configPath);
            case ["uninstall", var name]:
                return serviceManager.Uninstall(name);
            case ["start", var name]:
                return serviceManager.Start(name);
            case ["stop", var name]:
                return StopTunnel(name);
            case ["status", var name]:
                return serviceManager.Status(name);
            case ["agent-install", var target]:
                return serviceManager.InstallAgent(target);
            case ["agent-uninstall"]:
                return serviceManager.UninstallAgent();
            case ["agent-start"]:
                return serviceManager.StartAgent();
            case ["agent-stop"]:
                return serviceManager.StopAgent();
            case ["agent-status"]:
                return serviceManager.AgentStatus();
            case ["uapi-get", var name]:
                Console.WriteLine(uapi.Get(name));
                return 0;
            case ["tunnel-ip", var name, var ip]:
                return DebugTunnelIp(name, ip);
            case ["add-source", var kind, var url]:
                return await AddSourceAsync(kind, url);
            case ["list-sources"]:
                return await ListSourcesAsync();
            case ["update-sources"]:
                return await UpdateSourcesAsync();
            case ["remove-source", var name]:
                return await RemoveSourceAsync(name);
            case ["geo-files"]:
                return await ListGeoFilesAsync();
            case ["geo-query", var kind, var key]:
                return await GeoQueryAsync(kind, key);
            case ["set-geo", var name, var toggle, .. var rules]:
                return await SetGeoAsync(name, toggle, rules);
            case ["settings"]:
                return await ShowSettingsAsync();
            case ["set-option", var key, var value]:
                return await SetOptionAsync(key, value);
            case ["seed-domain", var name, var domain, var ip]:
                return await SeedDomainAsync(name, domain, ip);
            case ["domains", var name]:
                return await ListDomainsAsync(name);
            case ["config-add", var name, var path]:
                return ConfigAdd(name, path);
            case ["config-list"]:
                return ConfigList();
            case ["config-show", var name]:
                return await ConfigShowAsync(name);
            case ["config-copy", var source, var destination]:
                return await ConfigCopyAsync(source, destination);
            case ["config-edit", var name, var path]:
                return ConfigEdit(name, path);
            case ["config-remove", var name]:
                return await ConfigRemoveAsync(name);
            case ["balancer-add", var name, var recheck, .. var members]:
                return await BalancerAddAsync(name, recheck, members);
            case ["balancer-list"]:
                return await BalancerListAsync();
            case ["balancer-show", var name]:
                return await BalancerShowAsync(name);
            case ["balancer-remove", var name]:
                return await BalancerRemoveAsync(name);
            case ["balancer-mode", var name, var mode]:
                return await BalancerModeAsync(name, mode);
            case ["routing-list-add", var listName, .. var listRules]:
                return await RoutingListAddAsync(listName, listRules);
            case ["assign-routing", var profile, var list, var toggle]:
                return await AssignRoutingAsync(profile, list, toggle);
            case ["connect"]:
                return await IpcCmdAsync(IpcContract.OpSetConnection, ["connect"]);
            case ["disconnect"]:
                return await IpcCmdAsync(IpcContract.OpSetConnection, ["disconnect"]);
            case ["balancer-run", var name]:
                return await BalancerRunAsync(name);
            case ["ipc-probe"]:
                return await IpcProbeAsync();
            case ["ipc-cmd", var op, .. var cmdArgs]:
                return await IpcCmdAsync(op, cmdArgs);
            case ["balancer-state"]:
                return await BalancerStateAsync(null);
            case ["balancer-state", var name]:
                return await BalancerStateAsync(name);
            case ["backup", var path]:
                return await backupService.BackupAsync(path);
            case ["restore", var path]:
                return await backupService.RestoreAsync(path, false);
            case ["restore", var path, "--force"]:
                return await backupService.RestoreAsync(path, true);
            default:
                await RunDemoAsync();
                return 0;
        }
    }

    private async Task<int> AddSourceAsync(string kind, string url)
    {
        var sources = await store.ListGeoSourcesAsync();
        var position = sources.Count == 0 ? 1 : sources.Max(s => s.Position) + 1;
        var name = $"{kind.ToLowerInvariant()}-{position}";
        await store.SaveGeoSourceAsync(new GeoSource(name, kind.ToLowerInvariant(), url, position));
        Console.WriteLine($"added source {name} ({kind}) {url}");
        return 0;
    }

    private async Task<int> ListSourcesAsync()
    {
        foreach (var source in await store.ListGeoSourcesAsync())
        {
            Console.WriteLine($"{source.Position}\t{source.Name}\t{source.Kind}\t{source.Url}");
        }

        return 0;
    }

    private async Task<int> UpdateSourcesAsync()
    {
        var sources = await store.ListGeoSourcesAsync();
        await Task.WhenAll(sources.Select(async source =>
        {
            try
            {
                var metadata = await geoFileUpdater.UpdateAsync(source);
                Console.WriteLine($"updated {metadata.Name}: {metadata.CategoryCount} entries, sha {metadata.Sha256[..12]}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"failed {source.Name}: {ex.Message}");
            }
        }));

        await RematerializeAllAsync();
        await geoConfigurator.RematerializeAllRoutingListsAsync();
        return 0;
    }

    private async Task<int> RemoveSourceAsync(string name)
    {
        await store.RemoveGeoSourceAsync(name);
        var path = TunnelPaths.GeoDataFile(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        Console.WriteLine($"removed source {name}");
        return 0;
    }

    private async Task<int> ListGeoFilesAsync()
    {
        foreach (var metadata in await store.ListGeoFilesAsync())
        {
            Console.WriteLine($"{metadata.Name}\t{metadata.CategoryCount}\t{metadata.UpdatedAt:u}\t{metadata.SourceUrl}");
        }

        return 0;
    }

    private async Task<int> GeoQueryAsync(string kind, string key)
    {
        var index = GeoIndex.Load(await store.ListGeoSourcesAsync());
        if (kind.Equals("ip", StringComparison.OrdinalIgnoreCase))
        {
            var cidrs = index.Cidrs(key);
            Console.WriteLine($"{key}: {cidrs.Count} cidrs");
            foreach (var cidr in cidrs.Take(5))
            {
                Console.WriteLine(cidr);
            }

            return 0;
        }

        var domains = index.Domains(key);
        Console.WriteLine($"{key}: {domains.Count} domains");
        foreach (var domain in domains.Take(5))
        {
            Console.WriteLine($"{domain.Kind} {domain.Value}");
        }

        return 0;
    }

    private async Task<int> SetGeoAsync(string name, string toggle, string[] ruleArgs)
    {
        var split = toggle.Equals("on", StringComparison.OrdinalIgnoreCase);
        var rules = new List<GeoRule>();
        foreach (var arg in ruleArgs)
        {
            var rule = ParseRule(arg);
            if (rule is not null)
            {
                rules.Add(rule);
            }
        }

        var index = GeoIndex.Load(await store.ListGeoSourcesAsync());
        var (materializedRoutes, domains) = GeoMaterializer.Materialize(rules, index);
        await store.SaveTunnelGeoAsync(new TunnelGeo(name, split, rules, materializedRoutes, domains));
        Console.WriteLine($"set-geo {name}: split={split}, {rules.Count} rules -> {materializedRoutes.Count} routes, {domains.Count} domains");
        if (materializedRoutes.Count > 10000)
        {
            Console.WriteLine($"warning: {materializedRoutes.Count} routes is large (e.g. full-country geoip) and may strain the OS route table");
        }

        return 0;
    }

    private async Task<int> ShowSettingsAsync()
    {
        var settings = await settingsStore.LoadAsync();
        Console.WriteLine($"refresh-seconds\t{settings.RefreshSeconds}");
        Console.WriteLine($"connect-timeout-seconds\t{settings.ConnectTimeoutSeconds}");
        Console.WriteLine($"dead-threshold-seconds\t{settings.DeadThresholdSeconds}");
        Console.WriteLine($"failback-probes\t{settings.FailbackProbes}");
        Console.WriteLine($"probe-timeout-seconds\t{settings.ProbeTimeoutSeconds}");
        return 0;
    }

    private async Task<int> SetOptionAsync(string key, string value)
    {
        if (!await settingsStore.SetAsync(key, value))
        {
            Console.WriteLine($"invalid option or value; keys: {string.Join(", ", SettingsStore.Keys())}");
            return 1;
        }

        Console.WriteLine($"set {key} = {value}");
        return 0;
    }

    private async Task<int> SeedDomainAsync(string name, string domain, string ip)
    {
        await store.SaveDomainResolutionAsync(name, new DomainResolution(domain.ToLowerInvariant(), [ip]));
        Console.WriteLine($"seeded {name} {domain} -> {ip}");
        return 0;
    }

    private async Task<int> ListDomainsAsync(string name)
    {
        foreach (var resolution in await store.ListDomainResolutionsAsync(name))
        {
            Console.WriteLine($"{resolution.Domain} -> {string.Join(", ", resolution.Ips)}");
        }

        return 0;
    }

    private int ConfigAdd(string name, string path)
    {
        try
        {
            configRepo.Add(name, path);
            Console.WriteLine($"added config {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    private int ConfigList()
    {
        foreach (var name in configRepo.List())
        {
            var endpoint = EndpointLabel(File.ReadAllText(TunnelPaths.ConfigFile(name)));
            Console.WriteLine($"{name}\t{endpoint}\t{serviceManager.QueryState(name)}");
        }

        return 0;
    }

    private async Task<int> ConfigShowAsync(string name)
    {
        if (!configRepo.Exists(name))
        {
            Console.WriteLine($"unknown config: {name}");
            return 1;
        }

        var config = await File.ReadAllTextAsync(TunnelPaths.ConfigFile(name));
        Console.WriteLine($"config {name}");
        Console.WriteLine($"  endpoint: {EndpointLabel(config)}");
        Console.WriteLine($"  allowed:  {string.Join(", ", WgConfigEditor.GetAllowedIps(config))}");
        Console.WriteLine($"  service:  {serviceManager.QueryState(name)}");

        var geo = await store.GetTunnelGeoAsync(name);
        if (geo is not null)
        {
            Console.WriteLine($"  geo:      split={(geo.GeoSplit ? "on" : "off")}, {geo.Routes.Count} routes, {geo.Domains.Count} domains");
        }

        return 0;
    }

    private async Task<int> ConfigCopyAsync(string source, string destination)
    {
        try
        {
            await configRepo.CopyAsync(source, destination);
            Console.WriteLine($"copied config {source} -> {destination}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    private int ConfigEdit(string name, string path)
    {
        try
        {
            configRepo.Edit(name, path);
            Console.WriteLine($"updated config {name} (restart its tunnel to apply)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    private async Task<int> ConfigRemoveAsync(string name)
    {
        await configRepo.RemoveAsync(name);
        Console.WriteLine($"removed config {name}");
        return 0;
    }

    private async Task<int> BalancerAddAsync(string name, string recheck, string[] members)
    {
        if (!int.TryParse(recheck, out var seconds) || seconds <= 0)
        {
            Console.WriteLine("invalid recheck seconds");
            return 1;
        }

        if (members.Length == 0)
        {
            Console.WriteLine("at least one member required");
            return 1;
        }

        foreach (var member in members)
        {
            if (!configRepo.Exists(member))
            {
                Console.WriteLine($"unknown config: {member}");
                return 1;
            }
        }

        await store.SaveBalancerAsync(new BalancerGroup(name, seconds, members));
        Console.WriteLine($"saved balancer {name}: recheck={seconds}s, members={string.Join(" > ", members)}");
        return 0;
    }

    private async Task<int> BalancerListAsync()
    {
        foreach (var name in await store.ListBalancerNamesAsync())
        {
            var balancer = await store.GetBalancerAsync(name);
            if (balancer is not null)
            {
                Console.WriteLine($"{name}\t{balancer.Mode}\t{balancer.RecheckSeconds}s\t{string.Join(" > ", balancer.Members)}");
            }
        }

        return 0;
    }

    private async Task<int> BalancerShowAsync(string name)
    {
        var balancer = await store.GetBalancerAsync(name);
        if (balancer is null)
        {
            Console.WriteLine($"unknown balancer: {name}");
            return 1;
        }

        Console.WriteLine($"balancer {name} (mode {balancer.Mode}, recheck {balancer.RecheckSeconds}s)");
        for (var i = 0; i < balancer.Members.Count; i++)
        {
            var member = balancer.Members[i];
            var state = configRepo.Exists(member) ? serviceManager.QueryState(member) : "MISSING";
            Console.WriteLine($"  {i}. {member}\t{state}");
        }

        return 0;
    }

    private async Task<int> BalancerRemoveAsync(string name)
    {
        await store.RemoveBalancerAsync(name);
        Console.WriteLine($"removed balancer {name}");
        return 0;
    }

    private async Task<int> BalancerModeAsync(string name, string mode)
    {
        if (mode is not ("priority" or "latency" or "off"))
        {
            Console.WriteLine("mode must be priority, latency, or off");
            return 1;
        }

        var balancer = await store.GetBalancerAsync(name);
        if (balancer is null)
        {
            Console.WriteLine($"unknown balancer: {name}");
            return 1;
        }

        await store.SaveBalancerAsync(balancer with { Mode = mode });
        Console.WriteLine($"balancer {name} mode = {mode}");
        return 0;
    }

    private async Task<int> BalancerRunAsync(string name)
    {
        var balancer = await store.GetBalancerAsync(name);
        if (balancer is null)
        {
            Console.WriteLine($"unknown balancer: {name}");
            return 1;
        }

        using (var cts = new CancellationTokenSource())
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            await balancerRunner.RunAsync(balancer, cts.Token);
        }

        return 0;
    }

    private async Task<int> BalancerStateAsync(string? name)
    {
        if (name is null)
        {
            foreach (var state in await store.ListBalancerStatesAsync())
            {
                PrintState(state);
            }

            return 0;
        }

        var single = await store.GetBalancerStateAsync(name);
        if (single is null)
        {
            Console.WriteLine($"no live state for {name}");
            return 1;
        }

        PrintState(single);
        return 0;
    }

    private int StopTunnel(string name)
    {
        var code = serviceManager.Stop(name);
        reconciler.Reconcile();
        return code;
    }

    private static async Task<int> IpcProbeAsync()
    {
        var client = new StatusPipeClient();
        var received = new TaskCompletionSource<StatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Connected += () => Console.WriteLine("[probe] connected to pipe");
        client.Disconnected += () => Console.WriteLine("[probe] disconnected from pipe");
        client.SnapshotReceived += snapshot => received.TrySetResult(snapshot);

        using (var cts = new CancellationTokenSource())
        {
            var loop = client.RunAsync(cts.Token);
            var first = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(8)));
            cts.Cancel();
            await loop;

            if (first != received.Task)
            {
                Console.WriteLine("ipc-probe: no snapshot (is the agent running?)");
                return 1;
            }

            var snapshot = await received.Task;
            Console.WriteLine($"agent v{snapshot.AgentVersion} running={snapshot.Active} status={snapshot.BoundStatus} bound={snapshot.BoundTarget ?? "(none)"} selected={snapshot.SelectedTarget ?? "(none)"}");
            Console.WriteLine($"settings\trestart-required={snapshot.RestartRequired}\tbetter={snapshot.BetterMember ?? "(none)"}");
            foreach (var config in snapshot.Configs)
            {
                Console.WriteLine($"config\t{config.Name}\t{config.Endpoint}\tgeo={(config.GeoSplit ? "on" : "off")}\t{config.Status}");
            }

            foreach (var balancer in snapshot.Balancers)
            {
                var routing = balancer.UseRouting ? balancer.RoutingListId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "on" : "off";
                Console.WriteLine($"balancer\t{balancer.Name}\t{balancer.Mode}\t{balancer.Status}\tactive={balancer.ActiveMember ?? "(none)"}\tmembers=[{string.Join(", ", balancer.Members)}]\trouting={routing}");
            }

            foreach (var list in snapshot.RoutingLists ?? [])
            {
                Console.WriteLine($"routing-list\t{list.Id}\t{list.Name}\trules={list.RuleCount}\troutes={list.RouteCount}\tdomains={list.DomainCount}");
            }

            foreach (var source in snapshot.Sources ?? [])
            {
                Console.WriteLine($"source\t{source.Name}\t{source.Kind}\tupdated={source.Updated ?? "(never)"}\tcats={source.CategoryCount}");
            }

            return 0;
        }
    }

    private static async Task<int> IpcCmdAsync(string op, string[] args)
    {
        var client = new StatusPipeClient();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Connected += () => connected.TrySetResult();

        using (var cts = new CancellationTokenSource())
        {
            var loop = client.RunAsync(cts.Token);
            var ready = await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(8)));
            if (ready != connected.Task)
            {
                cts.Cancel();
                await loop;
                Console.WriteLine("ipc-cmd: could not connect (is the agent running?)");
                return 1;
            }

            var ack = await client.SendCommandAsync(new IpcCommand(op, args), cts.Token);
            Console.WriteLine($"ack: ok={ack.Ok} {ack.Message}");
            cts.Cancel();
            await loop;
            return ack.Ok ? 0 : 1;
        }
    }

    private int DebugTunnelIp(string name, string ip)
    {
        var config = File.ReadAllText(TunnelPaths.ConfigFile(name));
        var peer = WgConfigEditor.GetPeerPublicKey(config);
        var index = routes.FindInterfaceIndex(name);
        if (peer is null || index is null)
        {
            Console.WriteLine($"missing peer key or adapter: peer={peer is not null}, adapter={index is not null}");
            return 1;
        }

        var success = geoActivator.TunnelIp(name, peer, index.Value, IPAddress.Parse(ip));
        Console.WriteLine($"tunnel-ip {ip} via {name} (if {index}): {success}");
        return success ? 0 : 1;
    }

    private async Task<int> RoutingListAddAsync(string name, IReadOnlyList<string> rules)
    {
        var existing = await store.GetRoutingListByNameAsync(name);
        var id = await geoConfigurator.ApplyToRoutingListAsync(existing?.Id ?? 0, name, rules);
        Console.WriteLine($"routing-list {name}: id={id}, {rules.Count} rule(s)");
        return 0;
    }

    private async Task<int> AssignRoutingAsync(string profile, string list, string toggle)
    {
        if (await store.GetBalancerAsync(profile) is null)
        {
            Console.WriteLine($"unknown profile: {profile}");
            return 1;
        }

        long? listId = null;
        if (!list.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            var routingList = await store.GetRoutingListByNameAsync(list);
            if (routingList is null)
            {
                Console.WriteLine($"unknown routing list: {list}");
                return 1;
            }

            listId = routingList.Id;
        }

        var useRouting = toggle.Equals("on", StringComparison.OrdinalIgnoreCase);
        await store.SetProfileRoutingAsync(profile, listId, useRouting);
        Console.WriteLine($"assigned {profile}: list={list} use={(useRouting ? "on" : "off")}");
        return 0;
    }

    private async Task RematerializeAllAsync()
    {
        var index = GeoIndex.Load(await store.ListGeoSourcesAsync());
        foreach (var name in await store.ListTunnelGeoNamesAsync())
        {
            var geo = await store.GetTunnelGeoAsync(name);
            if (geo is null)
            {
                continue;
            }

            var (materializedRoutes, domains) = GeoMaterializer.Materialize(geo.Rules, index);
            await store.SaveTunnelGeoAsync(geo with { Routes = materializedRoutes, Domains = domains });
        }
    }

    private async Task RunDemoAsync()
    {
        var (publicKey, privateKey) = WireGuardEngine.GenerateKeypair();

        var profile = new TunnelProfile(
            Name: "default",
            PrivateKey: privateKey,
            PublicKey: publicKey,
            Endpoint: string.Empty,
            Rules: [new GeoRule(GeoRuleKind.GeoSite, "geosite:openai")]);

        await store.SaveProfileAsync(profile);
        var profiles = await store.ListProfileNamesAsync();

        Console.WriteLine("AmneziaGeo Windows host - hello");
        Console.WriteLine($"State DB: {TunnelPaths.StateDbFile()}");
        Console.WriteLine($"Profiles: {string.Join(", ", profiles)}");
        Console.WriteLine($"Generated public key: {publicKey}");
    }

    private static string EndpointLabel(string config)
    {
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Endpoint", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
            }
        }

        return "(none)";
    }

    private static GeoRule? ParseRule(string text)
    {
        var colon = text.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        var value = text[(colon + 1)..];
        var kind = text[..colon].ToLowerInvariant() switch
        {
            "geosite" => GeoRuleKind.GeoSite,
            "geoip" => GeoRuleKind.GeoIp,
            "domain" => GeoRuleKind.Domain,
            "cidr" => GeoRuleKind.Cidr,
            _ => (GeoRuleKind?)null,
        };
        return kind is null ? null : new GeoRule(kind.Value, value);
    }

    private static void PrintState(BalancerState state)
    {
        Console.WriteLine($"{state.Group}\t{state.Status}\t{state.ActiveMember ?? "(none)"}\t{state.UpdatedAt:u}");
    }
}
