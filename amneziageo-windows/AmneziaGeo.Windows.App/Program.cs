using System.Net;
using System.ServiceProcess;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Windows.Engine;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Windows host entry point.
/// </summary>
internal static class Program
{
    private const int _singleMemberRecheckSeconds = 60;

    private static async Task<int> Main(string[] args)
    {
        switch (args)
        {
            case ["--service", var name]:
                await TunnelRunner.RunAsync(name);
                return 0;
            case ["--agent", var target]:
                return await RunAgentAsync(target);
            case ["install", var name, var configPath]:
                return ServiceManager.Install(name, configPath);
            case ["uninstall", var name]:
                return ServiceManager.Uninstall(name);
            case ["start", var name]:
                return ServiceManager.Start(name);
            case ["stop", var name]:
                return ServiceManager.Stop(name);
            case ["status", var name]:
                return ServiceManager.Status(name);
            case ["agent-install", var target]:
                return ServiceManager.InstallAgent(target);
            case ["agent-uninstall"]:
                return ServiceManager.UninstallAgent();
            case ["agent-start"]:
                return ServiceManager.StartAgent();
            case ["agent-stop"]:
                return ServiceManager.StopAgent();
            case ["agent-status"]:
                return ServiceManager.AgentStatus();
            case ["uapi-get", var name]:
                Console.WriteLine(UapiClient.Get(name));
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
            case ["balancer-run", var name]:
                return await BalancerRunAsync(name);
            default:
                await RunDemoAsync();
                return 0;
        }
    }

    private static async Task<int> AddSourceAsync(string kind, string url)
    {
        var store = await OpenStoreAsync();
        var sources = await store.ListGeoSourcesAsync();
        var position = sources.Count == 0 ? 1 : sources.Max(s => s.Position) + 1;
        var name = $"{kind.ToLowerInvariant()}-{position}";
        await store.SaveGeoSourceAsync(new GeoSource(name, kind.ToLowerInvariant(), url, position));
        Console.WriteLine($"added source {name} ({kind}) {url}");
        return 0;
    }

    private static async Task<int> ListSourcesAsync()
    {
        var store = await OpenStoreAsync();
        foreach (var source in await store.ListGeoSourcesAsync())
        {
            Console.WriteLine($"{source.Position}\t{source.Name}\t{source.Kind}\t{source.Url}");
        }

        return 0;
    }

    private static async Task<int> UpdateSourcesAsync()
    {
        var store = await OpenStoreAsync();
        var updater = new GeoFileUpdater(store);
        foreach (var source in await store.ListGeoSourcesAsync())
        {
            try
            {
                var metadata = await updater.UpdateAsync(source);
                Console.WriteLine($"updated {metadata.Name}: {metadata.CategoryCount} entries, sha {metadata.Sha256[..12]}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"failed {source.Name}: {ex.Message}");
            }
        }

        await RematerializeAllAsync(store);
        return 0;
    }

    private static async Task<int> RemoveSourceAsync(string name)
    {
        var store = await OpenStoreAsync();
        await store.RemoveGeoSourceAsync(name);
        var path = TunnelPaths.GeoDataFile(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        Console.WriteLine($"removed source {name}");
        return 0;
    }

    private static async Task<int> ListGeoFilesAsync()
    {
        var store = await OpenStoreAsync();
        foreach (var metadata in await store.ListGeoFilesAsync())
        {
            Console.WriteLine($"{metadata.Name}\t{metadata.CategoryCount}\t{metadata.UpdatedAt:u}\t{metadata.SourceUrl}");
        }

        return 0;
    }

    private static async Task<int> GeoQueryAsync(string kind, string key)
    {
        var store = await OpenStoreAsync();
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

    private static async Task<int> SetGeoAsync(string name, string toggle, string[] ruleArgs)
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

        var store = await OpenStoreAsync();
        var index = GeoIndex.Load(await store.ListGeoSourcesAsync());
        var (routes, domains) = GeoMaterializer.Materialize(rules, index);
        await store.SaveTunnelGeoAsync(new TunnelGeo(name, split, rules, routes, domains));
        Console.WriteLine($"set-geo {name}: split={split}, {rules.Count} rules -> {routes.Count} routes, {domains.Count} domains");
        if (routes.Count > 10000)
        {
            Console.WriteLine($"warning: {routes.Count} routes is large (e.g. full-country geoip) and may strain the OS route table");
        }

        return 0;
    }

    private static async Task<int> ShowSettingsAsync()
    {
        var store = await OpenStoreAsync();
        var settings = await SettingsStore.LoadAsync(store);
        Console.WriteLine($"refresh-seconds\t{settings.RefreshSeconds}");
        Console.WriteLine($"connect-timeout-seconds\t{settings.ConnectTimeoutSeconds}");
        Console.WriteLine($"dead-threshold-seconds\t{settings.DeadThresholdSeconds}");
        return 0;
    }

    private static async Task<int> SetOptionAsync(string key, string value)
    {
        var store = await OpenStoreAsync();
        if (!await SettingsStore.SetAsync(store, key, value))
        {
            Console.WriteLine($"invalid option or value; keys: {string.Join(", ", SettingsStore.Keys())}");
            return 1;
        }

        Console.WriteLine($"set {key} = {value}");
        return 0;
    }

    private static async Task<int> SeedDomainAsync(string name, string domain, string ip)
    {
        var store = await OpenStoreAsync();
        await store.SaveDomainResolutionAsync(name, new DomainResolution(domain.ToLowerInvariant(), [ip]));
        Console.WriteLine($"seeded {name} {domain} -> {ip}");
        return 0;
    }

    private static async Task<int> ListDomainsAsync(string name)
    {
        var store = await OpenStoreAsync();
        foreach (var resolution in await store.ListDomainResolutionsAsync(name))
        {
            Console.WriteLine($"{resolution.Domain} -> {string.Join(", ", resolution.Ips)}");
        }

        return 0;
    }

    private static int ConfigAdd(string name, string path)
    {
        try
        {
            ConfigRepository.Add(name, path);
            Console.WriteLine($"added config {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int ConfigList()
    {
        foreach (var name in ConfigRepository.List())
        {
            var endpoint = EndpointLabel(File.ReadAllText(TunnelPaths.ConfigFile(name)));
            Console.WriteLine($"{name}\t{endpoint}\t{ServiceManager.QueryState(name)}");
        }

        return 0;
    }

    private static async Task<int> ConfigShowAsync(string name)
    {
        if (!ConfigRepository.Exists(name))
        {
            Console.WriteLine($"unknown config: {name}");
            return 1;
        }

        var config = await File.ReadAllTextAsync(TunnelPaths.ConfigFile(name));
        Console.WriteLine($"config {name}");
        Console.WriteLine($"  endpoint: {EndpointLabel(config)}");
        Console.WriteLine($"  allowed:  {string.Join(", ", WgConfigEditor.GetAllowedIps(config))}");
        Console.WriteLine($"  service:  {ServiceManager.QueryState(name)}");

        var store = await OpenStoreAsync();
        var geo = await store.GetTunnelGeoAsync(name);
        if (geo is not null)
        {
            Console.WriteLine($"  geo:      split={(geo.GeoSplit ? "on" : "off")}, {geo.Routes.Count} routes, {geo.Domains.Count} domains");
        }

        return 0;
    }

    private static async Task<int> ConfigCopyAsync(string source, string destination)
    {
        try
        {
            var store = await OpenStoreAsync();
            await ConfigRepository.CopyAsync(source, destination, store);
            Console.WriteLine($"copied config {source} -> {destination}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int ConfigEdit(string name, string path)
    {
        try
        {
            ConfigRepository.Edit(name, path);
            Console.WriteLine($"updated config {name} (restart its tunnel to apply)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> ConfigRemoveAsync(string name)
    {
        var store = await OpenStoreAsync();
        await ConfigRepository.RemoveAsync(name, store);
        Console.WriteLine($"removed config {name}");
        return 0;
    }

    private static async Task<int> BalancerAddAsync(string name, string recheck, string[] members)
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
            if (!ConfigRepository.Exists(member))
            {
                Console.WriteLine($"unknown config: {member}");
                return 1;
            }
        }

        var store = await OpenStoreAsync();
        await store.SaveBalancerAsync(new BalancerGroup(name, seconds, members));
        Console.WriteLine($"saved balancer {name}: recheck={seconds}s, members={string.Join(" > ", members)}");
        return 0;
    }

    private static async Task<int> BalancerListAsync()
    {
        var store = await OpenStoreAsync();
        foreach (var name in await store.ListBalancerNamesAsync())
        {
            var balancer = await store.GetBalancerAsync(name);
            if (balancer is not null)
            {
                Console.WriteLine($"{name}\t{balancer.RecheckSeconds}s\t{string.Join(" > ", balancer.Members)}");
            }
        }

        return 0;
    }

    private static async Task<int> BalancerShowAsync(string name)
    {
        var store = await OpenStoreAsync();
        var balancer = await store.GetBalancerAsync(name);
        if (balancer is null)
        {
            Console.WriteLine($"unknown balancer: {name}");
            return 1;
        }

        Console.WriteLine($"balancer {name} (recheck {balancer.RecheckSeconds}s)");
        for (var i = 0; i < balancer.Members.Count; i++)
        {
            var member = balancer.Members[i];
            var state = ConfigRepository.Exists(member) ? ServiceManager.QueryState(member) : "MISSING";
            Console.WriteLine($"  {i}. {member}\t{state}");
        }

        return 0;
    }

    private static async Task<int> BalancerRemoveAsync(string name)
    {
        var store = await OpenStoreAsync();
        await store.RemoveBalancerAsync(name);
        Console.WriteLine($"removed balancer {name}");
        return 0;
    }

    private static async Task<int> BalancerRunAsync(string name)
    {
        var store = await OpenStoreAsync();
        var balancer = await store.GetBalancerAsync(name);
        if (balancer is null)
        {
            Console.WriteLine($"unknown balancer: {name}");
            return 1;
        }

        var settings = await SettingsStore.LoadAsync(store);
        using (var cts = new CancellationTokenSource())
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            var runner = new BalancerRunner(balancer, settings.ConnectTimeoutSeconds, settings.DeadThresholdSeconds, Console.WriteLine);
            await runner.RunAsync(cts.Token);
        }

        return 0;
    }

    private static async Task<int> RunAgentAsync(string target)
    {
        var logger = new FileLogger(TunnelPaths.AgentLogFile());
        try
        {
            var store = await OpenStoreAsync();
            var group = await ResolveAgentGroupAsync(store, target);
            if (group is null)
            {
                logger.Log($"agent: unknown target {target}");
                return 1;
            }

            var settings = await SettingsStore.LoadAsync(store);
            ServiceBase.Run(new AgentService(group, settings.ConnectTimeoutSeconds, settings.DeadThresholdSeconds, logger));
            return 0;
        }
        catch (Exception ex)
        {
            logger.Log($"agent fatal: {ex}");
            return 1;
        }
    }

    private static async Task<BalancerGroup?> ResolveAgentGroupAsync(IStateStore store, string target)
    {
        var balancer = await store.GetBalancerAsync(target);
        if (balancer is not null)
        {
            return balancer;
        }

        if (ConfigRepository.Exists(target))
        {
            return new BalancerGroup(target, _singleMemberRecheckSeconds, [target]);
        }

        return null;
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

    private static async Task RematerializeAllAsync(IStateStore store)
    {
        var index = GeoIndex.Load(await store.ListGeoSourcesAsync());
        foreach (var name in await store.ListTunnelGeoNamesAsync())
        {
            var geo = await store.GetTunnelGeoAsync(name);
            if (geo is null)
            {
                continue;
            }

            var (routes, domains) = GeoMaterializer.Materialize(geo.Rules, index);
            await store.SaveTunnelGeoAsync(geo with { Routes = routes, Domains = domains });
        }
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

    private static async Task<IStateStore> OpenStoreAsync()
    {
        var path = TunnelPaths.StateDbFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new SqliteStateStore(path);
        await store.InitializeAsync();
        return store;
    }

    private static int DebugTunnelIp(string name, string ip)
    {
        var config = File.ReadAllText(TunnelPaths.ConfigFile(name));
        var peer = WgConfigEditor.GetPeerPublicKey(config);
        var index = RouteManager.FindInterfaceIndex(name);
        if (peer is null || index is null)
        {
            Console.WriteLine($"missing peer key or adapter: peer={peer is not null}, adapter={index is not null}");
            return 1;
        }

        var activator = new GeoActivator(name, peer, index.Value);
        var success = activator.TunnelIp(IPAddress.Parse(ip));
        Console.WriteLine($"tunnel-ip {ip} via {name} (if {index}): {success}");
        return success ? 0 : 1;
    }

    private static async Task RunDemoAsync()
    {
        var store = await OpenStoreAsync();

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
}
