using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Everything a hello of a server of ours brings lands the moment the server answers, with no connect ahead: the
/// proxy catalog is published for the configuration, the routing lists it hands out are held with their origin and
/// the one it marks default is put in use, a configuration whose server allows none but its own lists takes the
/// routing list and one of them, and the dns and the updates it names stay with the kept offer. Asked again, the
/// same answer replaces what it brought instead of doubling it.
/// </summary>
public sealed class HelloSettingsTests : IAsyncLifetime
{
    private const string Text = "[Interface]\nPrivateKey = a\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";

    private const string Answer = """
        {"server":"amneziageo","version":"1","client":"c","features":{
          "websocket":{"port":8446,"path":"tunnel"},
          "subscription":{"url":"https://sub.example/list","revision":"r1"},
          "sources":{"items":[{"name":"amneziageo","kind":"geoip","url":"https://geo.example/geoip.dat"}]},
          "presets":{"only":true,"lists":[
            {"name":"Office","rules":["proxy|geosite:youtube"],"id":"0f8fad5b-d9cb-469f-a165-70867728950e","default":true,"source":"vpn.example-awg1-office","updated":"2026-10-08T09:00:00+00:00"}]},
          "dns":{"mode":"server","servers":["1.1.1.1"],"resolver":["10.8.0.1"]},
          "updates":{"url":"https://upd.example/updates","default":true},
          "proxies":{"version":1,"source":"0c0f1bd0-44f5-4a52-9f1e-9a1e4f5a6b7c","items":[
            {"id":"edge","name":"Edge","transport":"websocket","host":"proxy.example","port":443,"path":"ws",
             "tls":{"serverName":"proxy.example","pin":""},"auth":{"scheme":"bearer","credential":"test"}}]}}}
        """;

    private const string First = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-settings-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync();
        await _store.SaveConfigAsync("office", Text);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
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

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TheAnswer_LandsWholeWithoutAConnect()
    {
        await Offers().AskAsync("office", Text, CancellationToken.None);

        var kept = await ServerOfferStore.ReadAsync(_store, "office", Text, CancellationToken.None);
        Assert.True(kept.Ours);
        Assert.Equal(8446, kept.WebSocketPort);
        Assert.Equal("tunnel", kept.WebSocketPath);
        Assert.Equal("server", kept.Dns()!.Mode);
        Assert.Equal("1.1.1.1", Assert.Single(kept.Dns()!.Servers));
        Assert.Equal("10.8.0.1", Assert.Single(kept.Dns()!.Resolver));
        Assert.Equal("https://upd.example/updates/", kept.Updates()!.Url);

        var state = await _store.GetTransportProxyStateAsync(CancellationToken.None);
        var published = Assert.Single(state.Publications, publication => publication.Publisher == "office" && publication.Slot == "catalog");
        Assert.Equal("proxy.example", Assert.Single(published.Records).Proxy.Host);
        Assert.Contains(state.Selections, selection => selection.Config == "office" && selection.Reference is null);

        var list = Assert.Single(await _store.ListRoutingListsAsync());
        var origin = Assert.Single(await _store.ListRoutingListOriginsAsync());
        Assert.Equal("Office", list.Name);
        Assert.Equal(First, origin.PresetId);
        Assert.Equal(list.Id, origin.ListId);
        Assert.Equal(list.Id, await ConfigRouting.SelectedListAsync(_store, "office", CancellationToken.None));
        Assert.Contains(await _store.ListGeoSourcesAsync(), source => source.Name == "amneziageo");

        var subscription = Assert.Single(await _store.ListSubscriptionsAsync());
        Assert.Equal("https://sub.example/list", subscription.Url);
        Assert.True(subscription.FromHello);
        Assert.Contains(await _store.ListSubscriptionMembersAsync(), member => member.ConfigName == "office");
    }

    [Fact]
    public async Task AConfigKeptOffTheLists_TakesTheOnesOfItsServer()
    {
        await _store.SetConfigTransportAsync(new ConfigTransport("office", false, UseRouting: false));
        var own = await new GeoConfigurator(_store, new NoFiles()).ApplyToRoutingListAsync(0, "Mine", ["proxy|geosite:openai"]);
        await ConfigRouting.SelectListAsync(_store, "office", own, CancellationToken.None);

        await Offers().AskAsync("office", Text, CancellationToken.None);

        var office = Assert.Single(await _store.ListRoutingListsAsync(), list => list.Name == "Office");
        Assert.True(await ServerListsOnly.RoutingOnAsync(_store, "office", Text, CancellationToken.None));
        Assert.True((await _store.GetConfigTransportAsync("office", CancellationToken.None))!.UseRouting);
        var inUse = await ConfigRouting.SelectedListAsync(_store, "office", CancellationToken.None);
        Assert.Equal(office.Id, ServerListsOnly.Switch(await ServerListsOnly.OfAsync(_store, "office", Text, CancellationToken.None), inUse));
        await ConfigRouting.SelectListAsync(_store, "office", office.Id, CancellationToken.None);
        Assert.Equal(office.Id, await ConfigRouting.SelectedListAsync(_store, "office", CancellationToken.None));
    }

    [Fact]
    public async Task TheSameAnswerAgain_ReplacesInsteadOfDoubling()
    {
        var offers = Offers();
        await offers.AskAsync("office", Text, CancellationToken.None);
        await offers.AskAsync("office", Text, CancellationToken.None);

        var state = await _store.GetTransportProxyStateAsync(CancellationToken.None);
        var published = Assert.Single(state.Publications, publication => publication.Publisher == "office" && publication.Slot == "catalog");
        Assert.Single(published.Records);
        Assert.Single(state.Selections, selection => selection.Config == "office");
        Assert.Single(await _store.ListRoutingListsAsync());
        Assert.Single(await _store.ListRoutingListOriginsAsync());
        Assert.Single(await _store.ListSubscriptionsAsync());
        Assert.Single(await _store.ListSubscriptionMembersAsync());
    }

    private ServerOffers Offers() => new(
        _store,
        ask: (_, _) => Task.FromResult(new HelloReply(ServerOffer.Parse(Answer), true)),
        geo: new GeoConfigurator(_store, new NoFiles()));

    // Файлов гео-баз нет.
    private sealed class NoFiles : IGeoFileStore
    {
        public byte[]? Read(string name) => null;

        public Stream? OpenRead(string name) => null;

        public Task WriteAsync(string name, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
    }
}
