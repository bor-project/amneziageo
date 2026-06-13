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
                TunnelRunner.Run(name);
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
            default:
                await RunDemoAsync();
                return 0;
        }
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
