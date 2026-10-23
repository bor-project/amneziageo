using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A server that allows none but its own routing lists: while its configuration stands up, the list in use is one of
/// them, an own list or none is refused, the configuration takes the routing list, and the window holds the switches
/// that would leave the lists of the server.
/// </summary>
public sealed class ServerListsOnlyTests : IAsyncLifetime
{
    private const string Text = "[Interface]\nPrivateKey = a\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";

    private const string First = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private const string Second = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private const string Morning = "2026-10-08T09:00:00+00:00";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-only-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync();
        await _store.SaveConfigAsync("office", Text);
        await _store.SaveConfigAsync("home", Text.Replace("vpn.example", "home.example", StringComparison.Ordinal));
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

    [Theory]
    [InlineData("{\"lists\":[],\"only\":true}", true)]
    [InlineData("{\"lists\":[],\"only\":false}", false)]
    [InlineData("{\"lists\":[]}", false)]
    [InlineData("[]", false)]
    public void TheOffer_TellsWhetherTheServerAllowsItsOwnListsAlone(string presets, bool only)
    {
        var offer = ServerOffer.Parse("{\"server\":\"amneziageo\",\"version\":\"1\",\"client\":\"c\",\"features\":{\"presets\":" + presets + "}}");

        Assert.Equal(only, offer.PresetsOnly);
    }

    [Fact]
    public async Task TheLimit_NamesTheListsOfTheServerAndPicksTheDefaultOne()
    {
        await AskAsync(true, Item(First, "Unblock", Morning), Item(Second, "Work", Morning, marked: true));
        var ids = await IdsAsync();

        var limit = await ServerListsOnly.OfAsync(_store, "office", CancellationToken.None);

        Assert.NotNull(limit);
        Assert.Equal("office", limit.Config);
        Assert.Equal([ids["Unblock"], ids["Work"]], limit.Lists);
        Assert.Equal(ids["Work"], limit.Pick);
    }

    [Fact]
    public async Task TheLimit_PicksTheFirstListWhereTheServerMarksNoneDefault()
    {
        await AskAsync(true, Item(First, "Unblock", Morning), Item(Second, "Work", Morning));
        var ids = await IdsAsync();

        var limit = await ServerListsOnly.OfAsync(_store, "office", CancellationToken.None);

        Assert.Equal(ids["Unblock"], limit!.Pick);
    }

    [Fact]
    public async Task AConfigTheStoreHoldsNoTextOf_IsLimitedByTheTextItComesWith()
    {
        await Asked(true, Item(First, "Unblock", Morning)).AskAsync("phone", Text, CancellationToken.None);
        await _store.SetConfigTransportAsync(new ConfigTransport("phone", false, UseRouting: false));

        Assert.NotNull(await ServerListsOnly.OfAsync(_store, "phone", Text, CancellationToken.None));
        Assert.Null(await ServerListsOnly.OfAsync(_store, "phone", CancellationToken.None));
        Assert.NotNull(await ServerListsOnly.RoutingOffRefusalAsync(_store, "phone", Text, false, CancellationToken.None));
        Assert.True(await ServerListsOnly.RoutingOnAsync(_store, "phone", Text, CancellationToken.None));
    }

    [Fact]
    public async Task AServerThatAllowsAnyList_SetsNoLimit()
    {
        await AskAsync(false, Item(First, "Unblock", Morning));

        Assert.Null(await ServerListsOnly.OfAsync(_store, "office", CancellationToken.None));
    }

    [Fact]
    public async Task AServerWhoseListsTheDeviceNoLongerHolds_SetsNoLimit()
    {
        await AskAsync(true, Item(First, "Unblock", Morning));
        await _store.RemoveRoutingListAsync((await IdsAsync())["Unblock"]);

        Assert.Null(await ServerListsOnly.OfAsync(_store, "office", CancellationToken.None));
    }

    [Fact]
    public async Task TheFirstConfigWhoseServerLimits_SetsTheLimit()
    {
        await AskAsync(true, Item(First, "Unblock", Morning));

        var limit = await ServerListsOnly.FirstAsync(_store, ["home", "office"], CancellationToken.None);

        Assert.Equal("office", limit!.Config);
    }

    [Fact]
    public async Task AnOwnListOrNone_IsRefusedWhileAListOfTheServerIsNot()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());
        await AskAsync(true, Item(First, "Unblock", Morning));
        var server = (await IdsAsync())["Unblock"];
        var own = await geo.ApplyToRoutingListAsync(0, "Mine", ["proxy|geosite:openai"]);
        var limit = await ServerListsOnly.OfAsync(_store, "office", CancellationToken.None);

        var refused = ServerListsOnly.Refusal(limit, own);

        Assert.NotNull(refused);
        Assert.False(refused.Ok);
        Assert.True(IpcMessage.TryParse(refused.Message, out var key, out var args));
        Assert.Equal("Agent_RoutingListsServerOnly", key);
        Assert.Equal(["office"], args);
        Assert.NotNull(ServerListsOnly.Refusal(limit, null));
        Assert.Null(ServerListsOnly.Refusal(limit, server));
        Assert.Null(ServerListsOnly.Refusal(null, own));
    }

    [Fact]
    public async Task AnOwnListInUse_GivesWayToTheListOfTheServer()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());
        await AskAsync(true, Item(First, "Unblock", Morning));
        var server = (await IdsAsync())["Unblock"];
        var own = await geo.ApplyToRoutingListAsync(0, "Mine", ["proxy|geosite:openai"]);
        var limit = await ServerListsOnly.OfAsync(_store, "office", CancellationToken.None);

        Assert.Equal(server, ServerListsOnly.Switch(limit, own));
        Assert.Equal(server, ServerListsOnly.Switch(limit, null));
        Assert.Null(ServerListsOnly.Switch(limit, server));
        Assert.Null(ServerListsOnly.Switch(null, own));
    }

    [Fact]
    public async Task AConfigOffTheRoutingList_TakesItUnderTheFlagAlone()
    {
        await AskAsync(true, Item(First, "Unblock", Morning));
        await _store.SetConfigTransportAsync(new ConfigTransport("office", false, UseRouting: false));
        await _store.SetConfigTransportAsync(new ConfigTransport("home", false, UseRouting: false));

        Assert.True(await ServerListsOnly.RoutingOnAsync(_store, "office", CancellationToken.None));
        Assert.False(await ServerListsOnly.RoutingOnAsync(_store, "office", CancellationToken.None));
        Assert.False(await ServerListsOnly.RoutingOnAsync(_store, "home", CancellationToken.None));
        Assert.True((await _store.GetConfigTransportAsync("office"))!.UseRouting);
        Assert.False((await _store.GetConfigTransportAsync("home"))!.UseRouting);
    }

    [Fact]
    public async Task TurningTheRoutingListOff_IsRefusedUnderTheFlag()
    {
        await AskAsync(true, Item(First, "Unblock", Morning));

        var refused = await ServerListsOnly.RoutingOffRefusalAsync(_store, "office", false, CancellationToken.None);

        Assert.NotNull(refused);
        Assert.Null(await ServerListsOnly.RoutingOffRefusalAsync(_store, "office", true, CancellationToken.None));
        Assert.Null(await ServerListsOnly.RoutingOffRefusalAsync(_store, "home", false, CancellationToken.None));
    }

    [Fact]
    public async Task TheFlagOfTheServer_IsToldToTheAgentWhenItChanges()
    {
        await AskAsync(false, Item(First, "Unblock", Morning));
        var told = new List<string>();
        Task Tell(string config)
        {
            told.Add(config);
            return Task.CompletedTask;
        }

        await Asked(true, Item(First, "Unblock", Morning)).AskEachAsync([("office", Text)], Tell, CancellationToken.None);
        await Asked(true, Item(First, "Unblock", Morning)).AskEachAsync([("office", Text)], Tell, CancellationToken.None);
        await Asked(false, Item(First, "Unblock", Morning)).AskEachAsync([("office", Text)], Tell, CancellationToken.None);

        Assert.Equal(["office", "office"], told);
    }

    [Fact]
    public void UnderTheLimit_TheSwitchesThatLeaveTheListsOfTheServerHold()
    {
        var routing = Routing();

        routing.Apply(Snapshot("office-awg1", [2, 3], selected: 2));

        Assert.Equal([true, true, false], routing.RoutingLists.Select(row => row.Locked));
        Assert.Equal(Loc.Instance.Get("Main_ServerListsOnlyOf", "office-awg1"), routing.RoutingLists[0].ActiveTip);
        Assert.Equal(Loc.Instance.Get("Main_CardActiveFlag"), routing.RoutingLists[2].ActiveTip);
        Assert.True(routing.HomeRoutingLocked);
        Assert.Equal(Loc.Instance.Get("Main_ServerListsOnlyOf", "office-awg1"), routing.HomeRoutingTip);
    }

    [Fact]
    public void WithoutTheLimit_EverySwitchIsFree()
    {
        var routing = Routing();
        routing.Apply(Snapshot("office-awg1", [2, 3], selected: 2));

        routing.Apply(Snapshot(string.Empty, null, selected: 2));

        Assert.All(routing.RoutingLists, row => Assert.False(row.Locked));
        Assert.False(routing.HomeRoutingLocked);
        Assert.Equal(Loc.Instance.Get("Main_HomeRoutingHint"), routing.HomeRoutingTip);
    }

    [Fact]
    public void AnOpenOwnList_HoldsItsSwitchUnderTheLimit()
    {
        var routing = Routing();
        routing.Apply(Snapshot("office-awg1", [2, 3], selected: 2));

        routing.EditRoutingList = routing.RoutingLists[0];
        var own = (routing.OpenListLocked, routing.OpenListLockTip);
        routing.EditRoutingList = routing.RoutingLists[2];

        Assert.Equal((true, Loc.Instance.Get("Main_ServerListsOnlyOf", "office-awg1")), own);
        Assert.False(routing.OpenListLocked);
        Assert.Null(routing.OpenListLockTip);
    }

    [Fact]
    public void TheHeldSwitchOfTheOpenList_SendsNothing()
    {
        var agent = new Silent();
        var routing = new MainWindowViewModel(agent, new UiPreferences { PresetRegions = "de" }).Routing;
        routing.Apply(Snapshot("office-awg1", [2, 3], selected: 2));
        routing.EditRoutingList = routing.RoutingLists[0];

        routing.UseOpenList = true;

        Assert.DoesNotContain(IpcContract.OpAssignRouting, agent.Sent);
        Assert.False(routing.UseOpenList);
    }

    [Fact]
    public void TheRoutingSwitchOfTheConfig_ShowsOnAndStaysCleanUnderTheFlag()
    {
        var transport = new ConfigTransportViewModel(new Silent(), "office", false, 1420, false, useRouting: false);

        transport.ServerListsOnly = true;

        Assert.True(transport.RoutingSwitch);
        Assert.False(transport.UseRouting);
        Assert.False(transport.IsDirty);
        Assert.Equal(Loc.Instance.Get("Main_ServerListsOnly"), transport.RoutingSwitchTip);
    }

    [Fact]
    public void TheRoutingSwitchOfTheConfig_IsFreeWithoutTheFlag()
    {
        var transport = new ConfigTransportViewModel(new Silent(), "office", false, 1420, false, useRouting: false);

        Assert.False(transport.RoutingSwitch);
        Assert.Null(transport.RoutingSwitchTip);
    }

    private Task AskAsync(bool only, params string[] items) => Asked(only, items).AskAsync("office", Text, CancellationToken.None);

    private ServerOffers Asked(bool only, params string[] items) =>
        new(
            _store,
            ask: (_, _) => Task.FromResult(new HelloReply(ServerOffer.Parse(Lists(only, items)), true)),
            geo: new GeoConfigurator(_store, new NoFiles()));

    private async Task<Dictionary<string, long>> IdsAsync() =>
        (await _store.ListRoutingListsAsync()).ToDictionary(list => list.Name, list => list.Id, StringComparer.Ordinal);

    private static string Lists(bool only, params string[] items) =>
        "{\"server\":\"amneziageo\",\"version\":\"1\",\"client\":\"c\",\"features\":{\"presets\":{\"lists\":["
        + string.Join(',', items) + "],\"only\":" + (only ? "true" : "false") + "}}}";

    // A list the way the server hands it out with its identifier.
    private static string Item(string id, string name, string updated, bool marked = false) =>
        "{\"name\":\"" + name + "\",\"rules\":[\"proxy|geosite:youtube\"],\"allUdp\":false,\"full\":false,\"id\":\"" + id
        + "\",\"updated\":\"" + updated + "\",\"default\":" + (marked ? "true" : "false") + ",\"source\":\"office-awg1\"}";

    // The window asks no region of its own: the regions are picked and the tunnel is up.
    private static RoutingViewModel Routing() =>
        new MainWindowViewModel(new Silent(), new UiPreferences { PresetRegions = "de" }).Routing;

    // An own list and two lists of the server of office-awg1, with the limit the agent reports.
    private static StatusSnapshot Snapshot(string limitedBy, IReadOnlyList<long>? lists, long selected) =>
        new(
            "1",
            null,
            [],
            RoutingLists:
            [
                new(1, "Mine", 1, 1, 1, ProxyRuleCount: 1),
                new(2, "Unblock", 1, 1, 1, ProxyRuleCount: 1, Source: "office-awg1"),
                new(3, "Work", 1, 1, 1, ProxyRuleCount: 1, Source: "office-awg1"),
            ],
            BoundStatus: ConnectionStatus.Connected,
            SelectedRoutingList: selected,
            ServerListsOf: limitedBy,
            ServerLists: lists);

    // No geo files.
    private sealed class NoFiles : IGeoFileStore
    {
        public byte[]? Read(string name) => null;

        public Stream? OpenRead(string name) => null;

        public Task WriteAsync(string name, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Answers every command with nothing and keeps what was sent.
    private sealed class Silent : IAgentConnection
    {
        public event Action? Connected
        {
            add { }
            remove { }
        }

        public event Action? Disconnected
        {
            add { }
            remove { }
        }

        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public List<string> Sent { get; } = [];

        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            Sent.Add(command.Op);

            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
