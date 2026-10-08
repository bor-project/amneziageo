using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The hello of a server of ours hands out geo sources and routing lists. A list the device knows by the identifier of
/// the server keeps what it holds until the owner takes the newer version; a list it holds none of is added, and put
/// in use when the server marks it so and none is in use. A server that names no identifiers has its lists added by
/// name, as before.
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

    private const string First = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private const string Second = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private const string Morning = "2026-10-08T09:00:00+00:00";

    private const string Noon = "2026-10-08T12:00:00+00:00";

    private static readonly DateTimeOffset MorningTime = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset NoonTime = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

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
    public void TheOffer_OfAServerThatNamesNoIdentifiers_ReadsAsBefore()
    {
        var presets = ServerOffer.Parse(Answer).Presets();

        Assert.All(presets, preset => Assert.Equal(string.Empty, preset.Id));
        Assert.All(presets, preset => Assert.Null(preset.Updated));
        Assert.All(presets, preset => Assert.False(preset.Default));
        Assert.All(presets, preset => Assert.Equal(string.Empty, preset.Source));
    }

    [Fact]
    public void TheOffer_NamesTheIdentifierTheTimeTheFlagAndTheSourceOfAList()
    {
        var presets = ServerOffer.Parse(Lists(
            Item(First.ToUpperInvariant(), "Unblock", "proxy|geosite:youtube", Morning, marked: true),
            Item("not an id", "Plain", "proxy|geosite:openai", Noon, marked: true),
            Item(First, "Twin", "proxy|geosite:openai", Noon))).Presets();

        Assert.Equal(["Unblock", "Plain"], presets.Select(preset => preset.Name));
        Assert.Equal(First, presets[0].Id);
        Assert.Equal(MorningTime, presets[0].Updated);
        Assert.True(presets[0].Default);
        Assert.Equal("vpn.example-awg1-office", presets[0].Source);
        Assert.Equal(string.Empty, presets[1].Id);
        Assert.Null(presets[1].Updated);
    }

    [Fact]
    public void AList_ReadsBackAsItWasRendered()
    {
        var list = ServerOffer.Parse(Lists(Item(First, "Unblock", "proxy|geosite:youtube", Morning, marked: true, allUdp: true))).Presets()[0];

        var read = OfferedPreset.Parse(list.ToPayload());

        Assert.Equal("Unblock", read!.Name);
        Assert.Equal(["proxy|geosite:youtube"], read.Rules);
        Assert.True(read.AllUdp);
        Assert.False(read.Full);
        Assert.Equal(First, read.Id);
        Assert.Equal(MorningTime, read.Updated);
        Assert.True(read.Default);
        Assert.Equal("vpn.example-awg1-office", read.Source);
        Assert.Null(OfferedPreset.Parse(string.Empty));
        Assert.Null(OfferedPreset.Parse("{"));
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
    }

    [Fact]
    public async Task ADeviceWithoutLists_KeepsSendingEverythingThroughTheTunnel()
    {
        await Offers().AskAsync("office", Text, CancellationToken.None);
        await Offers().AskAsync("office", Text, CancellationToken.None);

        Assert.Equal(2, (await _store.ListRoutingListsAsync()).Count);
        Assert.Null(await _store.GetSelectedRoutingListAsync());
        Assert.Empty(await _store.ListRoutingListOriginsAsync());
        Assert.All(await _store.ListRoutingListSummariesAsync(), summary => Assert.Equal(string.Empty, summary.Source));
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
    public async Task AListOfTheServer_IsAddedWithWhereItCameFrom()
    {
        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning, allUdp: true));

        var list = Assert.Single(await _store.ListRoutingListsAsync());
        var origin = Assert.Single(await _store.ListRoutingListOriginsAsync());
        var summary = Assert.Single(await _store.ListRoutingListSummariesAsync());
        Assert.Equal(["proxy|geosite:youtube"], list.Rules.Select(GeoConfigurator.FormatWithRole));
        Assert.True((await _store.GetRoutingSettingsAsync(list.Id))!.AllUdp);
        Assert.Equal(new RoutingListOrigin(list.Id, First, "vpn.example-awg1-office", MorningTime), origin);
        Assert.Equal("vpn.example-awg1-office", summary.Source);
        Assert.False(summary.HasUpdate);
        Assert.Null(await _store.GetSelectedRoutingListAsync());
    }

    [Fact]
    public async Task WithoutASourceFromTheServer_AListCarriesTheNameOfItsConfiguration()
    {
        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning, source: ""));

        Assert.Equal("office", Assert.Single(await _store.ListRoutingListSummariesAsync()).Source);
    }

    [Fact]
    public async Task AListMarkedDefault_IsPutInUseWhenNoneIs()
    {
        await AskAsync(
            Item(First, "Plain", "proxy|geosite:openai", Morning),
            Item(Second, "Unblock", "proxy|geosite:youtube", Morning, marked: true));

        var lists = await _store.ListRoutingListsAsync();
        Assert.Equal(["Plain", "Unblock"], lists.Select(list => list.Name));
        Assert.Equal(lists[1].Id, await _store.GetSelectedRoutingListAsync());
    }

    [Fact]
    public async Task OfTwoListsMarkedDefault_TheFirstIsPutInUse()
    {
        await AskAsync(
            Item(First, "Unblock", "proxy|geosite:youtube", Morning, marked: true),
            Item(Second, "Other", "proxy|geosite:openai", Morning, marked: true));

        var lists = await _store.ListRoutingListsAsync();
        Assert.Equal(lists[0].Id, await _store.GetSelectedRoutingListAsync());
    }

    [Fact]
    public async Task AListMarkedDefault_LeavesTheListInUseAlone()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());
        var own = await geo.ApplyToRoutingListAsync(0, "Mine", ["proxy|geosite:openai"]);
        await _store.SetSelectedRoutingListAsync(own);

        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning, marked: true));

        Assert.Equal(2, (await _store.ListRoutingListsAsync()).Count);
        Assert.Equal(own, await _store.GetSelectedRoutingListAsync());
    }

    [Fact]
    public async Task AListMarkedDefault_IsPutInUseOnlyWhenItIsAdded()
    {
        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning, marked: true));
        await _store.SetSelectedRoutingListAsync(null);

        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning, marked: true));
        await AskAsync(Item(First, "Unblock", "proxy|geosite:openai", Noon, marked: true));

        Assert.Null(await _store.GetSelectedRoutingListAsync());
    }

    [Fact]
    public async Task ANewerVersion_WaitsForTheOwner()
    {
        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning));

        await AskAsync(Item(First, "Unblock more", "proxy|geosite:openai", Noon, allUdp: true));

        var list = Assert.Single(await _store.ListRoutingListsAsync());
        var origin = Assert.Single(await _store.ListRoutingListOriginsAsync());
        Assert.Equal("Unblock", list.Name);
        Assert.Equal(["proxy|geosite:youtube"], list.Rules.Select(GeoConfigurator.FormatWithRole));
        Assert.Null(await _store.GetRoutingSettingsAsync(list.Id));
        Assert.Equal(MorningTime, origin.Updated);
        Assert.Equal("Unblock more", origin.Pending!.Name);
        Assert.Equal(NoonTime, origin.Pending.Updated);
        Assert.True(Assert.Single(await _store.ListRoutingListSummariesAsync()).HasUpdate);
    }

    [Theory]
    [InlineData(Morning)]
    [InlineData("2026-10-07T09:00:00+00:00")]
    public async Task AVersionThatIsNotNewer_ChangesNothing(string when)
    {
        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning));

        await AskAsync(Item(First, "Unblock", "proxy|geosite:openai", when));

        var origin = Assert.Single(await _store.ListRoutingListOriginsAsync());
        Assert.Null(origin.Pending);
        Assert.Equal(MorningTime, origin.Updated);
        Assert.False(Assert.Single(await _store.ListRoutingListSummariesAsync()).HasUpdate);
    }

    [Fact]
    public async Task ANewerVersionThatHoldsTheSame_IsTakenWithoutAsking()
    {
        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning));

        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Noon));

        var origin = Assert.Single(await _store.ListRoutingListOriginsAsync());
        Assert.Null(origin.Pending);
        Assert.Equal(NoonTime, origin.Updated);
    }

    [Fact]
    public async Task AVersionNewerThanTheOneThatWaits_TakesItsPlace()
    {
        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning));
        await AskAsync(Item(First, "Unblock", "proxy|geosite:openai", Noon));

        await AskAsync(Item(First, "Unblock", "proxy|geosite:telegram", "2026-10-08T15:00:00+00:00"));
        await AskAsync(Item(First, "Unblock", "proxy|geosite:openai", Noon));

        var origin = Assert.Single(await _store.ListRoutingListOriginsAsync());
        Assert.Equal(["proxy|geosite:telegram"], origin.Pending!.Rules);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUpdate_ReplacesTheListAndKeepsWhetherItIsInUse(bool inUse)
    {
        var geo = new GeoConfigurator(_store, new NoFiles());
        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning));
        var list = Assert.Single(await _store.ListRoutingListsAsync());
        await _store.SetRoutingSettingsAsync(new RoutingSettings(list.Id, "10.0.0.0/8", false));
        await _store.SetSelectedRoutingListAsync(inUse ? list.Id : null);
        await AskAsync(Item(First, "Unblock more", "direct|geoip:ru", Noon, allUdp: true, source: "vpn.example-awg0-office"));

        var updated = await UpdateAsync(geo, list.Id);
        var again = await UpdateAsync(geo, list.Id);

        var after = Assert.Single(await _store.ListRoutingListsAsync());
        var settings = await _store.GetRoutingSettingsAsync(list.Id);
        Assert.True(updated);
        Assert.False(again);
        Assert.Equal(list.Id, after.Id);
        Assert.Equal("Unblock more", after.Name);
        Assert.Equal(["direct|geoip:ru"], after.Rules.Select(GeoConfigurator.FormatWithRole));
        Assert.True(settings!.AllUdp);
        Assert.Equal("10.0.0.0/8", settings.Exclusions);
        Assert.Equal(inUse ? list.Id : null, await _store.GetSelectedRoutingListAsync());
        Assert.Equal(
            new RoutingListOrigin(list.Id, First, "vpn.example-awg0-office", NoonTime),
            Assert.Single(await _store.ListRoutingListOriginsAsync()));
        Assert.False(Assert.Single(await _store.ListRoutingListSummariesAsync()).HasUpdate);
    }

    [Fact]
    public async Task AnUpdate_KeepsTheNameAnotherListDoesNotCarry()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());
        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning));
        var list = Assert.Single(await _store.ListRoutingListsAsync());
        await geo.ApplyToRoutingListAsync(0, "Taken", ["proxy|geosite:openai"]);
        await AskAsync(Item(First, "Taken", "direct|geoip:ru", Noon));

        await UpdateAsync(geo, list.Id);

        var after = await _store.GetRoutingListAsync(list.Id);
        Assert.Equal("Unblock", after!.Name);
        Assert.Equal(["direct|geoip:ru"], after.Rules.Select(GeoConfigurator.FormatWithRole));
    }

    [Fact]
    public async Task AListWithoutANewerVersion_IsNotUpdated()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());
        var own = await geo.ApplyToRoutingListAsync(0, "Mine", ["proxy|geosite:openai"]);

        Assert.False(await UpdateAsync(geo, own));
        Assert.False(await UpdateAsync(geo, 4096));
    }

    [Fact]
    public async Task AListHeldUnderTheName_IsTiedToTheListOfTheServer()
    {
        await Offers().AskAsync("office", Text, CancellationToken.None);
        var held = await _store.ListRoutingListsAsync();

        await AskAsync(
            Item(First, "Unblock", "proxy|geosite:youtube\",\"direct|geoip:ru\",\"block|domain:ads.example", Noon, allUdp: true),
            Item(Second, "Everything", "direct|geoip:by", Noon));

        var origins = await _store.ListRoutingListOriginsAsync();
        var summaries = await _store.ListRoutingListSummariesAsync();
        Assert.Equal(held.Select(list => list.Id), (await _store.ListRoutingListsAsync()).Select(list => list.Id));
        Assert.Equal([First, Second], origins.Select(origin => origin.PresetId));
        Assert.Null(origins[0].Pending);
        Assert.Equal(NoonTime, origins[0].Updated);
        Assert.Equal(["direct|geoip:by"], origins[1].Pending!.Rules);
        Assert.Equal([false, true], summaries.Select(summary => summary.HasUpdate));
        Assert.All(summaries, summary => Assert.Equal("vpn.example-awg1-office", summary.Source));
        Assert.Null(await _store.GetSelectedRoutingListAsync());
    }

    [Fact]
    public async Task AListOfAnotherServerUnderTheSameName_GetsANameOfItsOwn()
    {
        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning));

        await AskAsync(Item(Second, "Unblock", "proxy|geosite:openai", Morning, source: "other.example-awg1-office"));

        var summaries = await _store.ListRoutingListSummariesAsync();
        Assert.Equal(["Unblock", "Unblock (2)"], summaries.Select(summary => summary.Name));
        Assert.Equal(["vpn.example-awg1-office", "other.example-awg1-office"], summaries.Select(summary => summary.Source));
    }

    [Fact]
    public async Task ARemovedListOfTheServer_ComesBack()
    {
        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning, marked: true));
        var first = Assert.Single(await _store.ListRoutingListsAsync());
        await _store.RemoveRoutingListAsync(first.Id);
        var gone = await _store.ListRoutingListOriginsAsync();

        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning, marked: true));

        var back = Assert.Single(await _store.ListRoutingListsAsync());
        Assert.Empty(gone);
        Assert.NotEqual(first.Id, back.Id);
        Assert.Equal(back.Id, await _store.GetSelectedRoutingListAsync());
        Assert.Equal(First, Assert.Single(await _store.ListRoutingListOriginsAsync()).PresetId);
    }

    [Fact]
    public async Task AListAnOlderBuildRemoved_ComesBackUnderItsIdentifier()
    {
        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning));
        var first = Assert.Single(await _store.ListRoutingListsAsync());
        await using (var connection = new SqliteConnection($"Data Source={_path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM routing_list_rules WHERE list_id = $id; DELETE FROM routing_lists WHERE id = $id;";
            command.Parameters.AddWithValue("$id", first.Id);
            await command.ExecuteNonQueryAsync();
        }

        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning));

        var back = Assert.Single(await _store.ListRoutingListsAsync());
        var origin = Assert.Single(await _store.ListRoutingListOriginsAsync());
        Assert.Equal(back.Id, origin.ListId);
        Assert.Equal(First, origin.PresetId);
    }

    [Fact]
    public async Task ASourceHeldUnderTheNameOfTheServer_TakesItsAddressOnce()
    {
        await _store.SaveGeoSourceAsync(new GeoSource("amneziageo", "geoip", "https://old.example/geoip.dat", 1));
        await _store.SaveGeoSourceAsync(new GeoSource("geosite-2", "geosite", "https://geo.example/geosite.dat", 2));
        var fetched = new List<string>();
        var offers = Offers(sources => fetched.AddRange(sources.Select(source => source.Url)));

        await offers.AskAsync("office", Text, CancellationToken.None);
        var moved = await _store.ListGeoSourcesAsync();
        await _store.SaveGeoSourceAsync(moved[0] with { Url = "https://mine.example/geoip.dat" });
        await offers.AskAsync("office", Text, CancellationToken.None);

        var after = await _store.ListGeoSourcesAsync();
        Assert.Equal(["amneziageo", "geosite-2"], moved.Select(source => source.Name));
        Assert.Equal("https://geo.example/geoip.dat", moved[0].Url);
        Assert.Equal(1, moved[0].Position);
        Assert.Equal(["https://geo.example/geoip.dat"], fetched);
        Assert.Equal(["amneziageo", "geosite-2"], after.Select(source => source.Name));
        Assert.Equal("https://mine.example/geoip.dat", after[0].Url);
    }

    [Fact]
    public async Task ASourceOfAnotherKindUnderTheName_IsLeftAlone()
    {
        await _store.SaveGeoSourceAsync(new GeoSource("amneziageo", "geosite", "https://old.example/geosite.dat", 1));

        await Offers().AskAsync("office", Text, CancellationToken.None);

        var sources = await _store.ListGeoSourcesAsync();
        Assert.Equal("https://old.example/geosite.dat", sources[0].Url);
        Assert.Contains(sources, source => source.Url == "https://geo.example/geoip.dat" && source.Name != "amneziageo");
    }

    [Fact]
    public async Task BeforeAConnect_AListMarkedDefaultIsInUseWhenTheQuestionReturns()
    {
        await Asked(Lists(Item(First, "Unblock", "proxy|geosite:youtube", Morning, marked: true)))
            .BeforeConnectAsync("office", Text, null, CancellationToken.None);

        var list = Assert.Single(await _store.ListRoutingListsAsync());
        Assert.Equal(list.Id, await _store.GetSelectedRoutingListAsync());
    }

    [Fact]
    public async Task BeforeAConnect_ANewerVersionOfAListIsSeenWhenTheQuestionReturns()
    {
        await AskAsync(Item(First, "Unblock", "proxy|geosite:youtube", Morning));

        await Asked(Lists(Item(First, "Unblock", "proxy|geosite:openai", Noon)))
            .BeforeConnectAsync("office", Text, null, CancellationToken.None);

        var summary = Assert.Single(await _store.ListRoutingListSummariesAsync());
        Assert.True(summary.HasUpdate);
    }

    [Fact]
    public async Task AListPutInUse_IsToldToWhoeverReroutes()
    {
        var told = 0;

        await Asked(Lists(Item(First, "Unblock", "proxy|geosite:youtube", Morning, marked: true)), rerouted: () => told++)
            .AskAsync("office", Text, CancellationToken.None);

        Assert.Equal(1, told);
    }

    [Fact]
    public async Task AListThatIsOnlyAdded_ReroutesNothing()
    {
        var told = 0;

        await Asked(Lists(Item(First, "Unblock", "proxy|geosite:youtube", Morning)), rerouted: () => told++)
            .AskAsync("office", Text, CancellationToken.None);

        Assert.Equal(0, told);
    }

    private Task AskAsync(params string[] items) => Asked(Lists(items)).AskAsync("office", Text, CancellationToken.None);

    // Берёт ожидающую версию так, как это делает агент: своими командами сохранения списка и его настроек.
    private async Task<bool> UpdateAsync(GeoConfigurator geo, long listId)
    {
        if (await OfferedLists.WaitingAsync(_store, listId, CancellationToken.None) is not { } waiting)
        {
            return false;
        }

        Assert.Equal(listId.ToString(System.Globalization.CultureInfo.InvariantCulture), waiting.SaveArgs[0]);
        await geo.ApplyToRoutingListAsync(listId, waiting.SaveArgs[1], [.. waiting.SaveArgs.Skip(2)]);
        await _store.SetRoutingSettingsAsync(new RoutingSettings(
            listId,
            waiting.SettingsArgs[1],
            waiting.SettingsArgs[2] == "on",
            waiting.SettingsArgs[3],
            waiting.SettingsArgs[4] == "on"));
        await OfferedLists.SettleAsync(_store, listId, CancellationToken.None);

        return true;
    }

    private ServerOffers Offers(Action<IReadOnlyList<GeoSource>>? fetch = null) => Asked(Answer, fetch);

    private ServerOffers Asked(string answer, Action<IReadOnlyList<GeoSource>>? fetch = null, Action? rerouted = null) =>
        new(
            _store,
            ask: (_, _) => Task.FromResult(new HelloReply(ServerOffer.Parse(answer), true)),
            geo: new GeoConfigurator(_store, new NoFiles()),
            fetch: fetch,
            rerouted: rerouted);

    private static string Lists(params string[] items) =>
        "{\"server\":\"amneziageo\",\"version\":\"1\",\"client\":\"c\",\"features\":{\"presets\":{\"lists\":[" + string.Join(',', items) + "]}}}";

    // Список, как его отдаёт сервер с идентификаторами; правило вставляется как есть.
    private static string Item(
        string id,
        string name,
        string rule,
        string updated,
        bool marked = false,
        bool allUdp = false,
        string source = "vpn.example-awg1-office") =>
        "{\"name\":\"" + name + "\",\"rules\":[\"" + rule + "\"],\"allUdp\":" + (allUdp ? "true" : "false") + ",\"full\":false,\"id\":\"" + id
        + "\",\"updated\":\"" + updated + "\",\"default\":" + (marked ? "true" : "false") + ",\"source\":\"" + source + "\"}";

    // Файлов гео-баз нет.
    private sealed class NoFiles : IGeoFileStore
    {
        public byte[]? Read(string name) => null;

        public Stream? OpenRead(string name) => null;

        public Task WriteAsync(string name, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
    }
}
