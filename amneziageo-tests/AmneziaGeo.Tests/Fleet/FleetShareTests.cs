using AmneziaGeo.Decl;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Windows.App.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// A rule names the server it rides, and every server of the set has to be told the same thing about it: the
/// one it names carries it, the others do not, and one addressed at nobody leaves the tunnels altogether.
/// Two servers carrying the same rule is the leak this answers.
/// </summary>
public sealed class FleetShareTests
{
    private const long List = 1;

    private static FleetControl Set()
    {
        var fleet = new FleetControl(new FleetLive());
        fleet.SetOrder(["alpha", "bravo", "charlie"]);
        fleet.SetRole("alpha", TunnelRoles.Primary);
        fleet.SetRole("bravo", TunnelRoles.Reserve);
        fleet.SetRole("charlie", TunnelRoles.Neutral);
        fleet.Add("alpha");
        fleet.Add("bravo");
        fleet.Add("charlie");
        return fleet;
    }

    private static RuleRoute To(string name, string? fallback = null)
    {
        return new RuleRoute(new RuleTarget(RuleTarget.Server, name),
            fallback is null ? RuleTarget.Default : RuleTarget.Parse(fallback));
    }

    [Fact]
    public void AutoIsThePrimary()
    {
        Assert.Equal("alpha", Set().Rides(RuleRoute.Default));
    }

    [Fact]
    public void WithoutAPrimaryAutoIsTheFirstReserveTheModeLists()
    {
        var fleet = Set();
        fleet.Remove("alpha");

        // Charlie is up and listed ahead of nobody, but it is out of the balancer.
        Assert.Equal("bravo", fleet.Rides(RuleRoute.Default));
    }

    [Fact]
    public void ANeutralServerIsRiddenOnlyWhenItIsNamed()
    {
        var fleet = Set();

        Assert.Equal("charlie", fleet.Rides(To("charlie")));
        Assert.Equal("alpha", fleet.Rides(new RuleRoute(new RuleTarget(RuleTarget.Best), RuleTarget.Default)));
    }

    [Fact]
    public void AServerTheSetDoesNotHoldFallsToTheOtherEnd()
    {
        var fleet = Set();

        Assert.Equal("bravo", fleet.Rides(To("delta", "bravo")));
        Assert.Equal("alpha", fleet.Rides(To("delta")));
        Assert.Equal(string.Empty, fleet.Rides(To("delta", "block")));
    }

    [Fact]
    public void TheQuickestToAnswerWinsTheBalancer()
    {
        var fleet = Set();
        var best = new RuleRoute(new RuleTarget(RuleTarget.Best), RuleTarget.Default);
        var trips = new Dictionary<string, int> { ["alpha"] = 90, ["bravo"] = 30, ["charlie"] = 1 };

        Assert.Equal("bravo", fleet.Rides(best, trips));

        // Nobody measured leaves the choice to the order, which is the answer auto gives.
        Assert.Equal("alpha", fleet.Rides(best, new Dictionary<string, int>()));
    }

    [Fact]
    public void AnUnaddressedRuleRidesThePrimaryAndNobodyElse()
    {
        var fleet = Set();
        IReadOnlyList<GeoRule> rules = [new(GeoRuleKind.GeoSite, "github"), new(GeoRuleKind.Cidr, "10.0.0.0/8", RouteRole.Direct)];

        // The primary is handed the list itself: a machine nobody addressed a rule on carries what it always did.
        Assert.Same(rules, fleet.Share("alpha", List, rules));

        // The second tunnel is not a second copy of the first: what is kept off the tunnel still reads the same
        // on it, but the tunnelled rule rides one server only.
        Assert.Equal([new GeoRule(GeoRuleKind.Cidr, "10.0.0.0/8", RouteRole.Direct)], fleet.Share("bravo", List, rules));
    }

    [Fact]
    public void OnlyTheServerARuleNamesCarriesIt()
    {
        var fleet = Set();
        fleet.SetTarget(FleetTargets.Key(List, "geosite:github"), To("bravo"));
        IReadOnlyList<GeoRule> rules =
        [
            new(GeoRuleKind.GeoSite, "github"),
            new(GeoRuleKind.GeoIp, "ru"),
            new(GeoRuleKind.Cidr, "10.0.0.0/8", RouteRole.Direct),
        ];

        var alpha = fleet.Share("alpha", List, rules);
        var bravo = fleet.Share("bravo", List, rules);

        Assert.Equal([new GeoRule(GeoRuleKind.GeoIp, "ru"), new GeoRule(GeoRuleKind.Cidr, "10.0.0.0/8", RouteRole.Direct)], alpha);
        Assert.Equal([new GeoRule(GeoRuleKind.GeoSite, "github"), new GeoRule(GeoRuleKind.Cidr, "10.0.0.0/8", RouteRole.Direct)], bravo);
    }

    [Fact]
    public void ARuleThatFallsToBlockIsDroppedByEveryServer()
    {
        var fleet = Set();
        fleet.SetTarget(FleetTargets.Key(List, "geosite:github"), To("delta", "block"));
        IReadOnlyList<GeoRule> rules = [new(GeoRuleKind.GeoSite, "github")];

        foreach (var name in new[] { "alpha", "bravo" })
        {
            Assert.Equal([new GeoRule(GeoRuleKind.GeoSite, "github", RouteRole.Block)], fleet.Share(name, List, rules));
        }
    }

    [Fact]
    public void ARuleNobodyAnswersForLeavesTheTunnels()
    {
        var fleet = Set();
        fleet.SetTarget(FleetTargets.Key(List, "geosite:github"), To("delta", "delta"));
        IReadOnlyList<GeoRule> rules = [new(GeoRuleKind.GeoSite, "github")];

        Assert.Empty(fleet.Share("alpha", List, rules));
        Assert.Empty(fleet.Share("bravo", List, rules));
    }

    [Fact]
    public void ReaddressingARuleMovesTheStamp()
    {
        var fleet = Set();
        var before = fleet.Stamp;

        fleet.SetTarget(FleetTargets.Key(List, "geosite:github"), To("bravo"));

        Assert.NotEqual(before, fleet.Stamp);
    }
}
