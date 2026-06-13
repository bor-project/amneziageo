using AmneziaGeo.Config;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;

var configStore = new ConfigStore("amneziageo.json");
var config = await configStore.LoadAsync();

var store = new SqliteStateStore(config.DatabasePath);
await store.InitializeAsync();

var profile = new TunnelProfile(
    Name: "default",
    PrivateKey: string.Empty,
    PublicKey: string.Empty,
    Endpoint: string.Empty,
    Rules: [new GeoRule(GeoRuleKind.GeoSite, "geosite:openai")]);

await store.SaveProfileAsync(profile);
var profiles = await store.ListProfileNamesAsync();

Console.WriteLine("AmneziaGeo Windows host - hello");
Console.WriteLine($"State DB: {config.DatabasePath}");
Console.WriteLine($"Profiles: {string.Join(", ", profiles)}");
