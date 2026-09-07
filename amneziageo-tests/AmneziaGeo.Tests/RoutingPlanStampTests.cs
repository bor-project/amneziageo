using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Готовый план переживает переключение: отпечаток списка меняется ровно тогда, когда план перестаёт годиться.
/// </summary>
public sealed class RoutingPlanStampTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-stamp-{Guid.NewGuid():N}.db");
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
    public async Task Stamp_CarriesGenerationAndRules()
    {
        var id = await _store.SaveRoutingListAsync(List("main", [new GeoRule(GeoRuleKind.GeoIp, "ru")], ["10.0.0.0/8"]));

        var stamp = await _store.GetRoutingListStampAsync(id);

        Assert.NotNull(stamp);
        Assert.Equal("main", stamp.Name);
        Assert.Equal(1, stamp.Generation);
        Assert.Equal(new GeoRule(GeoRuleKind.GeoIp, "ru"), Assert.Single(stamp.Rules));
    }

    [Fact]
    public async Task Stamp_IsNullWhenTheListIsGone()
    {
        Assert.Null(await _store.GetRoutingListStampAsync(404));
    }

    [Fact]
    public async Task Generation_StandsWhileTheBucketsAreTheSame()
    {
        var id = await _store.SaveRoutingListAsync(List("main", [new GeoRule(GeoRuleKind.GeoIp, "ru")], ["10.0.0.0/8"]));
        await _store.SaveRoutingListAsync(List("main", [new GeoRule(GeoRuleKind.GeoIp, "ru")], ["10.0.0.0/8"], id));

        var kept = await _store.GetRoutingListStampAsync(id);
        await _store.SaveRoutingListAsync(List("main", [new GeoRule(GeoRuleKind.GeoIp, "ru")], ["10.0.0.0/8", "172.16.0.0/12"], id));
        var moved = await _store.GetRoutingListStampAsync(id);

        Assert.Equal(1, kept!.Generation);
        Assert.Equal(2, moved!.Generation);
    }

    [Fact]
    public async Task Stamp_MovesWhenAnApplicationRuleIsAdded()
    {
        var id = await _store.SaveRoutingListAsync(List("main", [new GeoRule(GeoRuleKind.GeoIp, "ru")], ["10.0.0.0/8"]));
        var before = Mark(await _store.GetRoutingListStampAsync(id));

        // Правило приложения не разворачивается ни в диапазоны, ни в имена: поколение списка стоит на месте.
        await _store.SaveRoutingListAsync(List(
            "main",
            [new GeoRule(GeoRuleKind.GeoIp, "ru"), new GeoRule(GeoRuleKind.App, "pkg=org.telegram.messenger")],
            ["10.0.0.0/8"],
            id));
        var after = await _store.GetRoutingListStampAsync(id);

        Assert.Equal(1, after!.Generation);
        Assert.NotEqual(before, Mark(after));
    }

    [Fact]
    public void Mark_StandsForTheSameListAndSession()
    {
        var list = new RoutingListStamp(7, "main", 3, [new GeoRule(GeoRuleKind.GeoSite, "category-ads")]);
        var settings = new RoutingSettings(7, "1.1.1.1", false);

        Assert.Equal(
            RoutingPlanStamp.Of(list, settings, ["10.9.9.1/32"], true, true, 300),
            RoutingPlanStamp.Of(list, settings, ["10.9.9.1/32"], true, true, 300));
    }

    [Fact]
    public void Mark_MovesWithEverythingThePlanIsBuiltFrom()
    {
        var list = new RoutingListStamp(7, "main", 3, [new GeoRule(GeoRuleKind.GeoSite, "category-ads")]);
        var settings = new RoutingSettings(7, "1.1.1.1", false);
        var mark = RoutingPlanStamp.Of(list, settings, ["10.9.9.1/32"], true, true, 300);

        Assert.NotEqual(mark, RoutingPlanStamp.Of(list with { Generation = 4 }, settings, ["10.9.9.1/32"], true, true, 300));
        Assert.NotEqual(mark, RoutingPlanStamp.Of(list, settings with { AllUdp = true }, ["10.9.9.1/32"], true, true, 300));
        Assert.NotEqual(mark, RoutingPlanStamp.Of(list, settings with { UseGlobalProxy = true }, ["10.9.9.1/32"], true, true, 300));
        Assert.NotEqual(mark, RoutingPlanStamp.Of(list, settings with { Exclusions = "8.8.8.8" }, ["10.9.9.1/32"], true, true, 300));
        Assert.NotEqual(mark, RoutingPlanStamp.Of(list, settings, ["10.9.9.0/24"], true, true, 300));
        Assert.NotEqual(mark, RoutingPlanStamp.Of(list, settings, [], true, true, 300));
        Assert.NotEqual(mark, RoutingPlanStamp.Of(list, settings, ["10.9.9.1/32"], false, true, 300));
        Assert.NotEqual(mark, RoutingPlanStamp.Of(list, settings, ["10.9.9.1/32"], true, false, 300));
        Assert.NotEqual(mark, RoutingPlanStamp.Of(list, settings, ["10.9.9.1/32"], true, true, 600));
        Assert.NotEqual(mark, RoutingPlanStamp.Of(null, null, [], true, true, 300));
    }

    private static string Mark(RoutingListStamp? list) => RoutingPlanStamp.Of(list, null, [], true, true, 300);

    private static RoutingList List(string name, IReadOnlyList<GeoRule> rules, IReadOnlyList<string> routes, long id = 0) =>
        new(id, name, rules, routes, [], [], [], [], [], [], []);

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
