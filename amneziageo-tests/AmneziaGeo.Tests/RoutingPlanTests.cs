using AmneziaGeo.Decl;
using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// How a list is split across the servers that are up: fi carries the default route, de is up beside it, nl is
/// known but down.
/// </summary>
public sealed class RoutingPlanTests
{
    private static readonly ServerFleet Fleet = new(true, ["fi", "de", "nl"], ["fi", "de"]);

    private static readonly ServerFleet Nothing = new(true, ["fi", "de", "nl"], []);

    [Fact]
    public void RuleAddressingNoServer_RidesTheHeadOfTheFleet()
    {
        var plan = RoutingPlan.Build([Rule("ru")], Fleet);

        Assert.Equal(["ru"], Carried(plan, "fi"));
        Assert.Empty(Carried(plan, "de"));
    }

    [Fact]
    public void RuleRidesTheServerItNames()
    {
        var plan = RoutingPlan.Build([Rule("ru"), Named("netflix", "de")], Fleet);

        Assert.Equal(["ru"], Carried(plan, "fi"));
        Assert.Equal(["netflix"], Carried(plan, "de"));
    }

    [Fact]
    public void ServerUpCarryingNothing_KeepsAnEmptySet()
    {
        var plan = RoutingPlan.Build([Named("netflix", "fi")], Fleet);

        Assert.Equal(["fi", "de"], plan.Servers.Select(server => server.Server));
        Assert.Empty(Carried(plan, "de"));
    }

    [Fact]
    public void ServerThatIsDown_GetsNoShareAtAll()
    {
        var plan = RoutingPlan.Build([Named("netflix", "nl")], Fleet);

        Assert.DoesNotContain("nl", plan.Servers.Select(server => server.Server));
    }

    [Fact]
    public void RuleWhoseServerIsDown_FallsBackOntoTheHead()
    {
        var plan = RoutingPlan.Build([Named("netflix", "nl")], Fleet);

        Assert.Equal(["netflix"], Carried(plan, "fi"));
        Assert.Empty(plan.Blocked);
    }

    [Fact]
    public void RuleAskedToBlockWhileItsServerIsDown_IsCarriedByNobody()
    {
        var plan = RoutingPlan.Build([Fallback("netflix", "nl", RuleTargetMode.Block)], Fleet);

        Assert.Empty(Carried(plan, "fi"));
        Assert.Empty(Carried(plan, "de"));
        Assert.Equal(["netflix"], plan.Blocked.Select(rule => rule.Value));
    }

    [Fact]
    public void BlockingStillApplies_WithNoServerUpAtAll()
    {
        var plan = RoutingPlan.Build([Fallback("netflix", "nl", RuleTargetMode.Block)], Nothing);

        Assert.Empty(plan.Servers);
        Assert.Equal(["netflix"], plan.Blocked.Select(rule => rule.Value));
    }

    [Fact]
    public void RuleAskedToLeaveTheTunnel_IsCarriedByNobodyAndBlockedByNobody()
    {
        var plan = RoutingPlan.Build([Fallback("netflix", "nl", RuleTargetMode.Direct)], Fleet);

        Assert.Empty(Carried(plan, "fi"));
        Assert.Empty(plan.Blocked);
        Assert.Single(plan.Verdicts);
    }

    [Fact]
    public void WithNoServerUp_NobodyCarriesAnything()
    {
        var plan = RoutingPlan.Build([Rule("ru"), Named("netflix", "de")], Nothing);

        Assert.Empty(plan.Servers);
        Assert.Empty(plan.Blocked);
    }

    [Fact]
    public void DirectAndBlockRules_StayOutOfTheSplit()
    {
        var direct = new GeoRule(GeoRuleKind.GeoIp, "ru", RouteRole.Direct);
        var blocked = new GeoRule(GeoRuleKind.GeoSite, "ads", RouteRole.Block);

        var plan = RoutingPlan.Build([direct, blocked], Fleet);

        Assert.Empty(Carried(plan, "fi"));
        Assert.Empty(plan.Blocked);
        Assert.Empty(plan.Verdicts);
    }

    [Fact]
    public void WithSeveralServersOff_TheOneTunnelCarriesEverything()
    {
        var plan = RoutingPlan.Build([Rule("ru"), Named("netflix", "de")], ServerFleet.Single("fi"));

        Assert.Equal(["ru", "netflix"], Carried(plan, "fi"));
    }

    [Fact]
    public void RuleKeepsItsPlaceInTheList()
    {
        var plan = RoutingPlan.Build([Rule("ru"), Rule("cn"), Rule("ir")], Fleet);

        Assert.Equal(["ru", "cn", "ir"], Carried(plan, "fi"));
    }

    [Fact]
    public void EveryProxyRuleLeavesAVerdictForTheJournal()
    {
        var plan = RoutingPlan.Build([Rule("ru"), Named("netflix", "de"), Named("bbc", "se")], Fleet);

        Assert.Equal(3, plan.Verdicts.Count);
        Assert.Equal([TargetReason.Auto, TargetReason.Named, TargetReason.UnknownServer], plan.Verdicts.Select(verdict => verdict.Target.Reason));
    }

    [Fact]
    public void RuleNamingNothingTheLibraryHolds_RidesTheHead()
    {
        var plan = RoutingPlan.Build([Named("netflix", "se")], Fleet);

        Assert.Equal(["netflix"], Carried(plan, "fi"));
        Assert.True(plan.Verdicts[0].Target.Unresolved);
    }

    [Fact]
    public void HeadOfTheFleetTakesTheRulesThatAddressNoServer()
    {
        var plan = RoutingPlan.Build([Rule("ru")], new ServerFleet(true, ["fi", "de"], ["de", "fi"]));

        Assert.Equal(["ru"], Carried(plan, "de"));
        Assert.Empty(Carried(plan, "fi"));
    }

    private static IEnumerable<string> Carried(RoutingPlan plan, string server)
    {
        return plan.Servers.Where(entry => entry.Server == server).SelectMany(entry => entry.Rules).Select(rule => rule.Value);
    }

    private static GeoRule Rule(string value)
    {
        return new GeoRule(GeoRuleKind.GeoIp, value);
    }

    private static GeoRule Named(string value, string server)
    {
        return new GeoRule(GeoRuleKind.GeoSite, value, RouteRole.Proxy, RuleTargetMode.Server, server);
    }

    private static GeoRule Fallback(string value, string server, RuleTargetMode fallback)
    {
        return new GeoRule(GeoRuleKind.GeoSite, value, RouteRole.Proxy, RuleTargetMode.Server, server, fallback);
    }
}
