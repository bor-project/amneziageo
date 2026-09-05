using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Приложение попадает в ту корзину, куда его положили: через туннель или мимо него.
/// </summary>
public sealed class RoutingAppBucketsTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-appbucket-{Guid.NewGuid():N}.db");
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
    public async Task AppRules_LandInTheBucketTheirRoleNames()
    {
        List<GeoRule> rules =
        [
            new(GeoRuleKind.App, "pkg=org.telegram.messenger"),
            new(GeoRuleKind.App, "pkg=ru.oneme.app", RouteRole.Direct),
            new(GeoRuleKind.Cidr, "10.0.0.0/8", RouteRole.Direct),
        ];

        var id = await _store.SaveRoutingListAsync(new RoutingList(0, "main", rules, [], [], [], [], [], [], [], []));
        var list = await _store.GetRoutingListAsync(id);

        Assert.NotNull(list);
        Assert.Equal(["pkg=org.telegram.messenger"], list.Apps);
        Assert.Equal(["pkg=ru.oneme.app"], list.DirectApps);
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
