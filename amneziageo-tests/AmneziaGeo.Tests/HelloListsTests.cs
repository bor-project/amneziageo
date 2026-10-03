using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The hello of a server of ours hands out geo sources and routing lists: the device adds the ones it holds none of,
/// brings back the ones the owner removed and keeps the ones the owner changed; a fresh install gets the list of
/// unavailable sites once.
/// </summary>
public sealed class HelloListsTests : IAsyncLifetime
{
    private const string Text = "[Interface]\nPrivateKey = a\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";

    private const string Answer = """
        {"server":"amneziageo","version":"1","client":"c","features":{
          "sources":{"items":[
            {"name":"amneziageo","kind":"geoip","url":"https://geo.example/geoip.dat"},
            {"name":"Bad Name","kind":"geosite","url":"https://geo.example/geosite.dat"},
            {"name":"odd","kind":"mmdb","url":"https://geo.example/odd.dat"},
            {"name":"local","kind":"geoip","url":"file:///etc/passwd"}]},
          "presets":{"lists":[
            {"name":"Unblock","rules":["proxy|geosite:youtube","direct|geoip:ru","block|domain:ads.example","proxy|app:path=/usr/bin/x","geosite:bare","nope|cidr:1.2.3.0/24"],"allUdp":true,"full":false},
            {"name":"Everything","rules":["direct|geoip:ru"],"full":true},
            {"name":"","rules":["proxy|geosite:youtube"]},
            {"name":"Unblock","rules":[]}]}}}
        """;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-lists-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _store.ClearPool();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void TheOffer_NamesTheSourcesOfAKindAndAddressTheDeviceTakes()
    {
        var sources = ServerOffer.Parse(Answer).Sources();

        Assert.Equal(
            [new OfferedSource("amneziageo", "geoip", "https://geo.example/geoip.dat"), new OfferedSource("Bad Name", "geosite", "https://geo.example/geosite.dat")],
            sources);
    }

    [Fact]
    public void TheOffer_NamesListsByGeoKeyNetworkOrDomainOnly()
    {
        var presets = ServerOffer.Parse(Answer).Presets();

        Assert.Equal(["Unblock", "Everything"], presets.Select(preset => preset.Name));
        Assert.Equal(["proxy|geosite:youtube", "direct|geoip:ru", "block|domain:ads.example"], presets[0].Rules);
        Assert.True(presets[0].AllUdp);
        Assert.False(presets[0].Full);
        Assert.True(presets[1].Full);
    }

    [Fact]
    public async Task TheHello_AddsTheSourcesAndListsTheDeviceHoldsNoneOf()
    {
        await _store.SaveGeoSourceAsync(new GeoSource("geoip-1", "geoip", "HTTPS://GEO.EXAMPLE/geoip.dat", 1));
        var fetched = new List<string>();
        var offers = Offers(sources => fetched.AddRange(sources.Select(source => source.Name)));

        await offers.AskAsync("office", Text, CancellationToken.None);

        var sources = await _store.ListGeoSourcesAsync();
        Assert.Equal(["geoip-1", "geosite-2"], sources.Select(source => source.Name));
        Assert.Equal("https://geo.example/geosite.dat", sources[1].Url);
        Assert.Equal(["geosite-2"], fetched);
        var lists = await _store.ListRoutingListsAsync();
        Assert.Equal(["Unblock", "Everything"], lists.Select(list => list.Name));
        Assert.Equal(
            ["proxy|geosite:youtube", "direct|geoip:ru", "block|domain:ads.example"],
            lists[0].Rules.Select(GeoConfigurator.FormatWithRole));
        Assert.Equal(lists[0].Id, await _store.GetSelectedRoutingListAsync());
        Assert.True((await _store.GetRoutingSettingsAsync(lists[0].Id))!.AllUdp);
        Assert.True((await _store.GetRoutingSettingsAsync(lists[1].Id))!.UseGlobalProxy);
    }

    [Fact]
    public async Task TheHello_BringsBackWhatWasRemovedAndKeepsWhatWasChanged()
    {
        var offers = Offers();
        await offers.AskAsync("office", Text, CancellationToken.None);
        var lists = await _store.ListRoutingListsAsync();
        var geo = new GeoConfigurator(_store, new NoFiles());
        await geo.ApplyToRoutingListAsync(lists[0].Id, "Unblock", ["proxy|geosite:openai"]);
        await _store.RemoveRoutingListAsync(lists[1].Id);
        await _store.RemoveGeoSourceAsync("amneziageo");

        await offers.AskAsync("office", Text, CancellationToken.None);

        var after = await _store.ListRoutingListsAsync();
        Assert.Equal(["Unblock", "Everything"], after.Select(list => list.Name));
        Assert.Equal(["proxy|geosite:openai"], after[0].Rules.Select(GeoConfigurator.FormatWithRole));
        Assert.Contains(await _store.ListGeoSourcesAsync(), source => source.Name == "amneziageo");
        Assert.Equal(lists[0].Id, await _store.GetSelectedRoutingListAsync());
    }

    [Fact]
    public async Task ADeviceWithListsOfItsOwn_KeepsItsChoice()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());
        var own = await geo.ApplyToRoutingListAsync(0, "Mine", ["proxy|geosite:openai"]);
        await _store.SetSelectedRoutingListAsync(null);

        await Offers().AskAsync("office", Text, CancellationToken.None);

        Assert.Equal(3, (await _store.ListRoutingListsAsync()).Count);
        Assert.Null(await _store.GetSelectedRoutingListAsync());
        Assert.NotEqual(0, own);
    }

    [Fact]
    public async Task WithoutTheGeoOfTheDevice_TheHelloAddsNothing()
    {
        var offers = new ServerOffers(_store, ask: (_, _) => Task.FromResult(new HelloReply(ServerOffer.Parse(Answer), true)));

        await offers.AskAsync("office", Text, CancellationToken.None);

        Assert.Empty(await _store.ListRoutingListsAsync());
        Assert.Empty(await _store.ListGeoSourcesAsync());
    }

    [Fact]
    public async Task AFreshInstall_GetsTheListOfUnavailableSitesOnce()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());

        var first = await RoutingSeed.SeedAsync(_store, geo, "Unavailable sites", CancellationToken.None);
        var list = Assert.Single(await _store.ListRoutingListsAsync());
        await _store.RemoveRoutingListAsync(list.Id);
        var second = await RoutingSeed.SeedAsync(_store, geo, "Unavailable sites", CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal("Unavailable sites", list.Name);
        Assert.Equal(RoutingDefaults.Unavailable.Select(rule => "proxy|" + rule), list.Rules.Select(GeoConfigurator.FormatWithRole));
        Assert.Empty(await _store.ListRoutingListsAsync());
    }

    [Fact]
    public async Task AFreshInstall_AppliesTheListAtOnce()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());

        await RoutingSeed.SeedAsync(_store, geo, "Unavailable sites", CancellationToken.None);

        var list = Assert.Single(await _store.ListRoutingListsAsync());
        Assert.Equal(list.Id, await _store.GetSelectedRoutingListAsync());
        Assert.True((await _store.GetRoutingSettingsAsync(list.Id))!.AllUdp);
    }

    [Fact]
    public async Task AnInstallInUse_IsNotSeeded()
    {
        await _store.SaveConfigAsync("office", Text);
        var geo = new GeoConfigurator(_store, new NoFiles());

        var seeded = await RoutingSeed.SeedAsync(_store, geo, "Unavailable sites", CancellationToken.None);
        await _store.RemoveConfigAsync("office");
        var later = await RoutingSeed.SeedAsync(_store, geo, "Unavailable sites", CancellationToken.None);

        Assert.False(seeded);
        Assert.False(later);
        Assert.Empty(await _store.ListRoutingListsAsync());
    }

    private ServerOffers Offers(Action<IReadOnlyList<GeoSource>>? fetch = null) =>
        new(
            _store,
            ask: (_, _) => Task.FromResult(new HelloReply(ServerOffer.Parse(Answer), true)),
            geo: new GeoConfigurator(_store, new NoFiles()),
            fetch: fetch);

    // Файлов гео-баз нет.
    private sealed class NoFiles : IGeoFileStore
    {
        public byte[]? Read(string name) => null;

        public Stream? OpenRead(string name) => null;

        public Task WriteAsync(string name, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
    }
}
