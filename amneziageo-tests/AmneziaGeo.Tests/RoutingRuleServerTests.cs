using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Microsoft.Data.Sqlite;
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

        Assert.Equal(RuleTargetMode.Auto, rule.ServerMode);
        Assert.Equal(string.Empty, rule.Server);
        Assert.Equal(RuleTargetMode.Auto, rule.FallbackMode);
        Assert.Equal(string.Empty, rule.Fallback);
    }

    [Fact]
    public async Task Store_KeepsTheServerAndTheFallbackTheListWasSavedWith()
    {
        var saved = new GeoRule(GeoRuleKind.GeoIp, "ru", RouteRole.Proxy, RuleTargetMode.Server, "fi", RuleTargetMode.Server, "de");

        var read = await RoundTripAsync(saved);

        Assert.Equal(RuleTargetMode.Server, read.ServerMode);
        Assert.Equal("fi", read.Server);
        Assert.Equal(RuleTargetMode.Server, read.FallbackMode);
        Assert.Equal("de", read.Fallback);
    }

    [Theory]
    [InlineData(RuleTargetMode.Block)]
    [InlineData(RuleTargetMode.Direct)]
    [InlineData(RuleTargetMode.Best)]
    public async Task Store_KeepsAFallbackThatNamesNoServer(RuleTargetMode mode)
    {
        var read = await RoundTripAsync(new GeoRule(GeoRuleKind.Domain, "example.com", RouteRole.Proxy, RuleTargetMode.Server, "fi", mode));

        Assert.Equal("fi", read.Server);
        Assert.Equal(mode, read.FallbackMode);
        Assert.Equal(string.Empty, read.Fallback);
    }

    [Fact]
    public async Task Store_KeepsTheBestServerApartFromANamedOne()
    {
        var read = await RoundTripAsync(new GeoRule(GeoRuleKind.GeoIp, "ru", RouteRole.Proxy, RuleTargetMode.Best));

        Assert.Equal(RuleTargetMode.Best, read.ServerMode);
        Assert.Equal(string.Empty, read.Server);
    }

    [Fact]
    public async Task Store_KeepsAFallbackChosenUnderAnUnaddressedRule()
    {
        var read = await RoundTripAsync(new GeoRule(GeoRuleKind.GeoIp, "ru", RouteRole.Proxy, RuleTargetMode.Auto, "", RuleTargetMode.Block));

        Assert.Equal(RuleTargetMode.Auto, read.ServerMode);
        Assert.Equal(RuleTargetMode.Block, read.FallbackMode);
    }

    [Theory]
    [InlineData(RouteRole.Direct)]
    [InlineData(RouteRole.Block)]
    public async Task Store_LeavesNoServerOnARoleThatNeverReadsOne(RouteRole role)
    {
        var read = await RoundTripAsync(new GeoRule(GeoRuleKind.Cidr, "10.0.0.0/8", role, RuleTargetMode.Server, "fi", RuleTargetMode.Server, "de"));

        Assert.Equal(RuleTargetMode.Auto, read.ServerMode);
        Assert.Equal(string.Empty, read.Server);
        Assert.Equal(RuleTargetMode.Auto, read.FallbackMode);
        Assert.Equal(string.Empty, read.Fallback);
    }

    [Fact]
    public async Task Store_DropsAFallbackNameTheModeDoesNotRead()
    {
        var read = await RoundTripAsync(new GeoRule(GeoRuleKind.GeoSite, "youtube", RouteRole.Proxy, RuleTargetMode.Server, "  fi  ", RuleTargetMode.Auto, "de"));

        Assert.Equal("fi", read.Server);
        Assert.Equal(RuleTargetMode.Auto, read.FallbackMode);
        Assert.Equal(string.Empty, read.Fallback);
    }

    [Fact]
    public async Task Store_ReadsANamedServerLeftBehindWithoutItsMode()
    {
        var id = await _store.SaveRoutingListAsync(new RoutingList(0, "legacy",
            [new GeoRule(GeoRuleKind.GeoIp, "ru", RouteRole.Proxy, RuleTargetMode.Server, "fi")], [], [], [], [], [], [], []));
        ClearServerMode(id);

        var list = await _store.GetRoutingListAsync(id);

        Assert.NotNull(list);
        var rule = Assert.Single(list!.Rules);
        Assert.Equal(RuleTargetMode.Server, rule.ServerMode);
        Assert.Equal("fi", rule.Server);
    }

    [Fact]
    public async Task Store_KeepsRulesInTheOrderTheyWereGiven()
    {
        var id = await _store.SaveRoutingListAsync(new RoutingList(0, "main",
            [
                new GeoRule(GeoRuleKind.GeoIp, "ru", RouteRole.Proxy, RuleTargetMode.Server, "fi"),
                new GeoRule(GeoRuleKind.GeoIp, "de", RouteRole.Proxy, RuleTargetMode.Server, "de"),
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

    // A row as the previous build left it: the name alone, no mode beside it.
    private void ClearServerMode(long listId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE routing_list_rules SET server_mode = '' WHERE list_id = $id;";
        command.Parameters.AddWithValue("$id", listId);
        command.ExecuteNonQuery();
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
