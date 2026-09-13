using AmneziaGeo.Dal;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Windows.App.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// Адрес правила живёт, пока живёт правило туннельной корзины, и следует за конфигурацией и при выключенном режиме.
/// </summary>
public sealed class AddressCleanupTests : IAsyncLifetime
{
    private const long List = 1;
    private const long OtherList = 2;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-targets-{Guid.NewGuid():N}.db");
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
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void ARuleGoneFromTheTunnelBucket_TakesItsAddressAlong()
    {
        var targets = Addressed();

        Assert.True(FleetTargets.KeepRules(targets, List, Tokens("geoip:ru")));

        Assert.Equal([FleetTargets.Key(List, "geoip:ru"), FleetTargets.Key(OtherList, "geosite:github")], targets.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AListKeepingEveryRule_KeepsEveryAddress()
    {
        var targets = Addressed();

        Assert.False(FleetTargets.KeepRules(targets, List, Tokens("geosite:github", "geoip:ru")));

        Assert.Equal(3, targets.Count);
    }

    [Fact]
    public void ARemovedList_LeavesNoAddressBehind()
    {
        var targets = Addressed();

        Assert.True(FleetTargets.KeepRules(targets, List, Tokens()));

        Assert.Equal([FleetTargets.Key(OtherList, "geosite:github")], targets.Keys);
    }

    [Fact]
    public void TheSet_ForgetsTheAddressOfADeletedRule()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetTarget(FleetTargets.Key(List, "geosite:github"), To("bravo"));

        Assert.True(fleet.KeepRules(List, Tokens("geoip:ru")));

        Assert.Equal(RuleRoute.Default, fleet.TargetOf(FleetTargets.Key(List, "geosite:github")));
        Assert.True(fleet.Moved);
    }

    [Fact]
    public async Task TheStoredSet_FollowsARenameWhileTheModeIsOff()
    {
        var running = new FleetControl(new FleetLive());
        running.SetTarget(FleetTargets.Key(List, "geosite:github"), To("bravo"));
        await FleetStore.WriteAsync(_store, running.Snapshot(), new Dictionary<string, string>(StringComparer.Ordinal));

        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        var stored = new FleetControl(new FleetLive());
        stored.Restore(await FleetStore.ReadAsync(_store, written));
        Assert.True(stored.Rename("bravo", "delta"));
        await FleetStore.WriteAsync(_store, stored.Snapshot(), written);

        var read = await FleetStore.ReadAsync(_store, new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.Equal(To("delta"), read.Targets[FleetTargets.Key(List, "geosite:github")]);
    }

    [Fact]
    public void AServerTheLibraryNoLongerHolds_IsStruckFromEveryPartOfTheState()
    {
        var targets = Addressed();
        var roles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["alpha"] = TunnelRoles.Primary,
            ["sub-c"] = TunnelRoles.Reserve,
        };
        var state = new FleetState(["alpha", "sub-c"], roles, "alpha", ["sub-c"], targets, ["sub-c"]);

        var kept = state.Within(Tokens("alpha"));

        Assert.True(state.Servers().SetEquals(["alpha", "bravo", "sub-c"]));
        Assert.True(kept.Servers().SetEquals(["alpha"]));
        Assert.Equal(["alpha"], kept.Order);
        Assert.Equal(["alpha"], kept.Roles.Keys);
        Assert.Equal("alpha", kept.Primary);
        Assert.Empty(kept.Desired);
        Assert.Empty(kept.Resume!);
        Assert.Equal(new RuleRoute(RuleTarget.Default, new RuleTarget(RuleTarget.Direct)), kept.Targets[FleetTargets.Key(List, "geosite:github")]);
    }

    [Fact]
    public void AnEndNamingAGoneServer_IsLeftToTheMachine()
    {
        var targets = new Dictionary<string, RuleRoute>(StringComparer.Ordinal)
        {
            [FleetTargets.Key(List, "geoip:ru")] = new RuleRoute(new RuleTarget(RuleTarget.Server, "alpha"), new RuleTarget(RuleTarget.Server, "sub-c")),
        };

        Assert.True(FleetTargets.KeepServers(targets, Tokens("alpha")));
        Assert.False(FleetTargets.KeepServers(targets, Tokens("alpha")));

        Assert.Equal(new RuleRoute(new RuleTarget(RuleTarget.Server, "alpha"), RuleTarget.Default), targets[FleetTargets.Key(List, "geoip:ru")]);
    }

    // Два правила первого списка и одно второго, все с адресом.
    private static Dictionary<string, RuleRoute> Addressed()
    {
        return new Dictionary<string, RuleRoute>(StringComparer.Ordinal)
        {
            [FleetTargets.Key(List, "geosite:github")] = To("bravo"),
            [FleetTargets.Key(List, "geoip:ru")] = To("alpha"),
            [FleetTargets.Key(OtherList, "geosite:github")] = To("bravo"),
        };
    }

    private static HashSet<string> Tokens(params string[] tokens)
    {
        return new HashSet<string>(tokens, StringComparer.Ordinal);
    }

    private static RuleRoute To(string server)
    {
        return new RuleRoute(new RuleTarget(RuleTarget.Server, server), new RuleTarget(RuleTarget.Direct));
    }
}
