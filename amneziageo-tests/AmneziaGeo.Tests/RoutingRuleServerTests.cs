using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The server a rule rides and the fallback it takes while that server is down, against a real SQLite store:
/// what the list is saved with is what it is read back with, and a role that never reads a server keeps none.
/// </summary>
public sealed class RoutingRuleServerTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-ruleserver-{Guid.NewGuid():N}.db");
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
    public void Rule_RidesWhicheverServerCarriesTheRouteByDefault()
    {
        var rule = new GeoRule(GeoRuleKind.GeoIp, "ru");

        Assert.Equal(string.Empty, rule.Server);
        Assert.Equal(RuleFallback.Auto, rule.FallbackMode);
        Assert.Equal(string.Empty, rule.Fallback);
    }

    [Fact]
    public async Task Store_KeepsTheServerAndTheFallbackTheListWasSavedWith()
    {
        var saved = new GeoRule(GeoRuleKind.GeoIp, "ru", RouteRole.Proxy, "fi", RuleFallback.Server, "de");

        var read = await RoundTripAsync(saved);

        Assert.Equal("fi", read.Server);
        Assert.Equal(RuleFallback.Server, read.FallbackMode);
        Assert.Equal("de", read.Fallback);
    }

    [Fact]
    public async Task Store_KeepsBlockingFallbackApartFromNamingOne()
    {
        var read = await RoundTripAsync(new GeoRule(GeoRuleKind.Domain, "example.com", RouteRole.Proxy, "fi", RuleFallback.None));

        Assert.Equal("fi", read.Server);
        Assert.Equal(RuleFallback.None, read.FallbackMode);
        Assert.Equal(string.Empty, read.Fallback);
    }

    [Theory]
    [InlineData(RouteRole.Direct)]
    [InlineData(RouteRole.Block)]
    public async Task Store_LeavesNoServerOnARoleThatNeverReadsOne(RouteRole role)
    {
        var read = await RoundTripAsync(new GeoRule(GeoRuleKind.Cidr, "10.0.0.0/8", role, "fi", RuleFallback.Server, "de"));

        Assert.Equal(string.Empty, read.Server);
        Assert.Equal(RuleFallback.Auto, read.FallbackMode);
        Assert.Equal(string.Empty, read.Fallback);
    }

    [Fact]
    public async Task Store_DropsAFallbackNameTheModeDoesNotRead()
    {
        var read = await RoundTripAsync(new GeoRule(GeoRuleKind.GeoSite, "youtube", RouteRole.Proxy, "  fi  ", RuleFallback.Auto, "de"));

        Assert.Equal("fi", read.Server);
        Assert.Equal(RuleFallback.Auto, read.FallbackMode);
        Assert.Equal(string.Empty, read.Fallback);
    }

    [Fact]
    public async Task Store_KeepsRulesInTheOrderTheyWereGiven()
    {
        var id = await _store.SaveRoutingListAsync(new RoutingList(0, "main",
            [
                new GeoRule(GeoRuleKind.GeoIp, "ru", RouteRole.Proxy, "fi"),
                new GeoRule(GeoRuleKind.GeoIp, "de", RouteRole.Proxy, "de"),
            ],
            [], [], [], [], [], [], []));

        var list = await _store.GetRoutingListAsync(id);

        Assert.NotNull(list);
        Assert.Equal(["fi", "de"], list!.Rules.Select(rule => rule.Server));
    }

    private async Task<GeoRule> RoundTripAsync(GeoRule rule)
    {
        var id = await _store.SaveRoutingListAsync(new RoutingList(0, $"list-{Guid.NewGuid():N}", [rule], [], [], [], [], [], [], []));
        var list = await _store.GetRoutingListAsync(id);

        Assert.NotNull(list);
        return Assert.Single(list!.Rules);
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
