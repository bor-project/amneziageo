using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Живая перезагрузка правил читает долю участника, а не список целиком.
/// </summary>
public sealed class RoutingShareStoreTests : IAsyncLifetime
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
    public async Task Materialization_GivesTheTunnelItsShareOfTheList()
    {
        var listId = await _store.SaveRoutingListAsync(new RoutingList(
            0,
            "main",
            [],
            ["10.0.0.0/8", "192.168.0.0/16"],
            [new GeoDomain(GeoDomainKind.Domain, "alpha.example"), new GeoDomain(GeoDomainKind.Domain, "bravo.example")],
            [],
            ["172.16.0.0/12"],
            [new GeoDomain(GeoDomainKind.Domain, "direct.example")],
            ["203.0.113.0/24"],
            [new GeoDomain(GeoDomainKind.Domain, "block.example")]));

        // Второе прокси-правило уехало на соседний туннель, и здесь оно стало прямым.
        await _store.SaveTunnelProjectionAsync(
            "alpha",
            true,
            ["10.0.0.0/8"],
            [new GeoDomain(GeoDomainKind.Domain, "alpha.example")],
            [],
            ["172.16.0.0/12", "192.168.0.0/16"],
            [new GeoDomain(GeoDomainKind.Domain, "direct.example"), new GeoDomain(GeoDomainKind.Domain, "bravo.example")],
            ["203.0.113.0/24"],
            [new GeoDomain(GeoDomainKind.Domain, "block.example")],
            listId);

        var current = await _store.GetActiveRoutingListMaterializationAsync("alpha");

        Assert.NotNull(current);
        Assert.Equal(listId, current.ListId);
        Assert.Equal(["10.0.0.0/8"], current.Routes);
        Assert.Equal([new GeoDomain(GeoDomainKind.Domain, "alpha.example")], current.Domains);
        Assert.Equal(["172.16.0.0/12", "192.168.0.0/16"], current.DirectRoutes);
        Assert.Equal([new GeoDomain(GeoDomainKind.Domain, "direct.example"), new GeoDomain(GeoDomainKind.Domain, "bravo.example")], current.DirectDomains);
        Assert.Equal(["203.0.113.0/24"], current.BlockRoutes);
        Assert.Equal([new GeoDomain(GeoDomainKind.Domain, "block.example")], current.BlockDomains);
    }

    [Fact]
    public async Task Materialization_StampsTheCutWhileTheListStandsStill()
    {
        var listId = await _store.SaveRoutingListAsync(new RoutingList(
            0,
            "main",
            [],
            ["10.0.0.0/8", "192.168.0.0/16"],
            [new GeoDomain(GeoDomainKind.Domain, "alpha.example")],
            [],
            [],
            [],
            [],
            []));

        await _store.SaveTunnelProjectionAsync(
            "alpha",
            true,
            ["10.0.0.0/8", "192.168.0.0/16"],
            [new GeoDomain(GeoDomainKind.Domain, "alpha.example")],
            [],
            [],
            [],
            [],
            [],
            listId);

        var whole = await _store.GetActiveRoutingListMaterializationAsync("alpha");

        // Второе правило уехало на соседний туннель; список тот же, поколение не двигалось.
        await _store.SaveTunnelProjectionAsync(
            "alpha",
            true,
            ["10.0.0.0/8"],
            [new GeoDomain(GeoDomainKind.Domain, "alpha.example")],
            [],
            [],
            [],
            [],
            [],
            listId);

        var part = await _store.GetActiveRoutingListMaterializationAsync("alpha");

        Assert.NotNull(whole);
        Assert.NotNull(part);
        Assert.Equal(whole.Generation, part.Generation);
        Assert.NotEqual(whole.Share, part.Share);
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
