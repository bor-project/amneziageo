using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// What a running tunnel is handed: its own share of the list, not the whole of it, and the blocking bucket the
/// machine shares.
/// </summary>
public sealed class TunnelShareTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-share-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _store = new SqliteStateStore(_dbPath);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _store.ClearPool();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Tunnel_ReadsTheShareItWasHanded()
    {
        var id = await SaveListAsync();

        await _store.SaveTunnelProjectionAsync("fi", true, ["1.0.0.0/8"], [Domain("netflix.com")], ["chrome.exe"], id, [], []);

        var current = await _store.GetActiveRoutingListMaterializationAsync("fi");
        Assert.NotNull(current);
        Assert.Equal(id, current!.ListId);
        Assert.Equal(["1.0.0.0/8"], current.Routes);
        Assert.Equal([Domain("netflix.com")], current.Domains);
    }

    [Fact]
    public async Task TwoTunnels_ReadDifferentSharesOfTheSameList()
    {
        var id = await SaveListAsync();

        await _store.SaveTunnelProjectionAsync("fi", true, ["1.0.0.0/8"], [], [], id, [], []);
        await _store.SaveTunnelProjectionAsync("de", true, ["2.0.0.0/8"], [], [], id, [], []);

        Assert.Equal(["1.0.0.0/8"], (await _store.GetActiveRoutingListMaterializationAsync("fi"))!.Routes);
        Assert.Equal(["2.0.0.0/8"], (await _store.GetActiveRoutingListMaterializationAsync("de"))!.Routes);
    }

    [Fact]
    public async Task BlockingBucket_ReachesTheTunnelWithTheShare()
    {
        var id = await SaveListAsync();

        await _store.SaveTunnelProjectionAsync("fi", true, ["1.0.0.0/8"], [], [], id, ["3.0.0.0/8"], [Domain("ads.example")]);

        var current = await _store.GetActiveRoutingListMaterializationAsync("fi");
        Assert.NotNull(current);
        Assert.Equal(["3.0.0.0/8"], current!.BlockRoutes);
        Assert.Equal([Domain("ads.example")], current.BlockDomains);
    }

    [Fact]
    public async Task ShareIsRewrittenInPlace()
    {
        var id = await SaveListAsync();

        await _store.SaveTunnelProjectionAsync("fi", true, ["1.0.0.0/8"], [], [], id, ["3.0.0.0/8"], []);
        await _store.SaveTunnelProjectionAsync("fi", true, ["4.0.0.0/8"], [], [], id, [], []);

        var current = await _store.GetActiveRoutingListMaterializationAsync("fi");
        Assert.NotNull(current);
        Assert.Equal(["4.0.0.0/8"], current!.Routes);
        Assert.Empty(current.BlockRoutes);
    }

    [Fact]
    public async Task ShareStamp_MovesWhenTheSameListIsDealtOutDifferently()
    {
        var id = await SaveListAsync();

        await _store.SaveTunnelProjectionAsync("fi", true, ["1.0.0.0/8"], [], [], id, [], []);
        var before = await _store.GetActiveRoutingListMaterializationAsync("fi");

        await _store.SaveTunnelProjectionAsync("fi", true, ["1.0.0.0/8", "2.0.0.0/8"], [], [], id, [], []);
        var after = await _store.GetActiveRoutingListMaterializationAsync("fi");

        Assert.Equal(before!.Generation, after!.Generation);
        Assert.NotEqual(before.Share, after.Share);
    }

    [Fact]
    public async Task ShareStamp_MovesWhenOnlyTheBlockingBucketChanges()
    {
        var id = await SaveListAsync();

        await _store.SaveTunnelProjectionAsync("fi", true, ["1.0.0.0/8"], [], [], id, [], []);
        var before = await _store.GetActiveRoutingListMaterializationAsync("fi");

        await _store.SaveTunnelProjectionAsync("fi", true, ["1.0.0.0/8"], [], [], id, ["3.0.0.0/8"], []);
        var after = await _store.GetActiveRoutingListMaterializationAsync("fi");

        Assert.NotEqual(before!.Share, after!.Share);
    }

    [Fact]
    public async Task ShareStamp_StaysPutWhenTheSetDidNotMove()
    {
        var id = await SaveListAsync();

        await _store.SaveTunnelProjectionAsync("fi", true, ["1.0.0.0/8"], [Domain("netflix.com")], [], id, ["3.0.0.0/8"], []);
        var before = await _store.GetActiveRoutingListMaterializationAsync("fi");

        await _store.SaveTunnelProjectionAsync("fi", true, ["1.0.0.0/8"], [Domain("netflix.com")], [], id, ["3.0.0.0/8"], []);
        var after = await _store.GetActiveRoutingListMaterializationAsync("fi");

        Assert.Equal(before!.Share, after!.Share);
    }

    [Fact]
    public async Task TunnelCarryingEverything_ReadsNoShare()
    {
        await _store.SaveTunnelProjectionAsync("fi", false, [], [], [], null, [], []);

        Assert.Null(await _store.GetActiveRoutingListMaterializationAsync("fi"));
    }

    private async Task<long> SaveListAsync()
    {
        return await _store.SaveRoutingListAsync(new RoutingList(0, "main",
            [new GeoRule(GeoRuleKind.GeoIp, "ru")],
            ["1.0.0.0/8", "2.0.0.0/8"], [], [], [], [], [], []));
    }

    private static GeoDomain Domain(string value)
    {
        return new GeoDomain(GeoDomainKind.Domain, value);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
