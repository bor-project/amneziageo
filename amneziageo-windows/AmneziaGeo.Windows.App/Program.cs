using System.Net;
using AmneziaGeo.Config;
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
    private static async Task<int> Main(string[] args)
    {
        switch (args)
        {
            case ["--service", var name]:
                await TunnelRunner.RunAsync(name);
                return 0;
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
            case ["seed-domain", var name, var domain, var ip]:
                return await SeedDomainAsync(name, domain, ip);
            case ["domains", var name]:
                return await ListDomainsAsync(name);
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
        var configStore = new ConfigStore("amneziageo.json");
        var config = await configStore.LoadAsync();

        var store = new SqliteStateStore(config.DatabasePath);
        await store.InitializeAsync();

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
        Console.WriteLine($"State DB: {config.DatabasePath}");
        Console.WriteLine($"Profiles: {string.Join(", ", profiles)}");
        Console.WriteLine($"Generated public key: {publicKey}");
    }
}
