using AmneziaGeo.Decl;
using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The table that resolves the server a rule rides: fi and de are up in that order, nl is known but down, and
/// se answers to nothing.
/// </summary>
public sealed class TargetResolverTests
{
    private static readonly ServerFleet Fleet = new(true, ["fi", "de", "nl"], ["fi", "de"]);

    private static readonly ServerFleet Nothing = new(true, ["fi", "de", "nl"], []);

    [Fact]
    public void SingleServerMode_IgnoresTheServerARuleNames()
    {
        var fleet = new ServerFleet(false, ["fi", "de"], ["fi", "de"]);

        var target = TargetResolver.Resolve(Named("de"), fleet);

        Assert.Equal(TargetKind.Auto, target.Kind);
        Assert.Equal(TargetReason.SingleServer, target.Reason);
        Assert.False(target.Unresolved);
    }

    [Fact]
    public void FleetOfOne_ResolvesEverythingToTheDefaultRoute()
    {
        var target = TargetResolver.Resolve(Named("de"), ServerFleet.Single("fi"));

        Assert.Equal(TargetKind.Auto, target.Kind);
        Assert.Equal(TargetReason.SingleServer, target.Reason);
    }

    [Fact]
    public void RuleWithoutAServer_TakesTheDefaultRoute()
    {
        var target = TargetResolver.Resolve(Rule(RuleTargetMode.Auto, string.Empty), Fleet);

        Assert.Equal(TargetKind.Auto, target.Kind);
        Assert.Equal(TargetReason.Auto, target.Reason);
    }

    [Fact]
    public void RuleRidesTheServerItNames()
    {
        var target = TargetResolver.Resolve(Named("de"), Fleet);

        Assert.Equal(new RuleTarget(TargetKind.Server, "de", TargetReason.Named), target);
    }

    [Fact]
    public void RuleAskingForTheBest_RidesTheFirstServerUp()
    {
        var target = TargetResolver.Resolve(Rule(RuleTargetMode.Best, string.Empty), Fleet);

        Assert.Equal(new RuleTarget(TargetKind.Server, "fi", TargetReason.Best), target);
    }

    [Fact]
    public void BestFollowsThePriorityItWasGiven()
    {
        var reordered = new ServerFleet(true, ["fi", "de", "nl"], ["de", "fi"]);

        var target = TargetResolver.Resolve(Rule(RuleTargetMode.Best, string.Empty), reordered);

        Assert.Equal("de", target.Server);
    }

    [Fact]
    public void RuleNamingNobody_TakesTheDefaultRouteAndIsWorthAWarning()
    {
        var target = TargetResolver.Resolve(Named("se"), Fleet);

        Assert.Equal(TargetKind.Auto, target.Kind);
        Assert.Equal(TargetReason.UnknownServer, target.Reason);
        Assert.True(target.Unresolved);
    }

    [Fact]
    public void ServerNamesAreComparedExactly()
    {
        var target = TargetResolver.Resolve(Named("DE"), Fleet);

        Assert.Equal(TargetReason.UnknownServer, target.Reason);
    }

    [Fact]
    public void RuleWhoseServerIsDown_TakesTheDefaultRouteByDefault()
    {
        var target = TargetResolver.Resolve(Named("nl"), Fleet);

        Assert.Equal(TargetKind.Auto, target.Kind);
        Assert.Equal(TargetReason.FallbackAuto, target.Reason);
        Assert.False(target.Unresolved);
    }

    [Fact]
    public void RuleWhoseServerIsDown_BlocksWhenToldTo()
    {
        var target = TargetResolver.Resolve(Rule(RuleTargetMode.Server, "nl", RuleTargetMode.Block), Fleet);

        Assert.Equal(new RuleTarget(TargetKind.Block, "", TargetReason.FallbackBlocked), target);
    }

    [Fact]
    public void RuleWhoseServerIsDown_GoesPastTheTunnelWhenToldTo()
    {
        var target = TargetResolver.Resolve(Rule(RuleTargetMode.Server, "nl", RuleTargetMode.Direct), Fleet);

        Assert.Equal(new RuleTarget(TargetKind.Direct, "", TargetReason.FallbackDirect), target);
    }

    [Fact]
    public void RuleWhoseServerIsDown_TakesTheBestWhenToldTo()
    {
        var target = TargetResolver.Resolve(Rule(RuleTargetMode.Server, "nl", RuleTargetMode.Best), Fleet);

        Assert.Equal(new RuleTarget(TargetKind.Server, "fi", TargetReason.FallbackBest), target);
    }

    [Fact]
    public void RuleWhoseServerIsDown_RidesItsSecondChoice()
    {
        var target = TargetResolver.Resolve(SecondChoice("nl", "fi"), Fleet);

        Assert.Equal(new RuleTarget(TargetKind.Server, "fi", TargetReason.FallbackServer), target);
    }

    [Fact]
    public void RuleWhoseSecondChoiceIsDownToo_TakesTheDefaultRoute()
    {
        var down = new ServerFleet(true, ["fi", "de", "nl"], ["de"]);

        var target = TargetResolver.Resolve(SecondChoice("nl", "fi"), down);

        Assert.Equal(TargetKind.Auto, target.Kind);
        Assert.Equal(TargetReason.FallbackDown, target.Reason);
        Assert.False(target.Unresolved);
    }

    [Theory]
    [InlineData("se")]
    [InlineData("")]
    public void RuleWhoseSecondChoiceNamesNobody_TakesTheDefaultRouteAndIsWorthAWarning(string fallback)
    {
        var target = TargetResolver.Resolve(SecondChoice("nl", fallback), Fleet);

        Assert.Equal(TargetKind.Auto, target.Kind);
        Assert.Equal(fallback.Length == 0 ? TargetReason.FallbackAuto : TargetReason.UnknownFallback, target.Reason);
    }

    [Theory]
    [InlineData(RuleTargetMode.Auto, "")]
    [InlineData(RuleTargetMode.Best, "")]
    [InlineData(RuleTargetMode.Server, "nl")]
    public void WithNoServerUp_EverythingGoesPastTheTunnel(RuleTargetMode mode, string server)
    {
        var target = TargetResolver.Resolve(Rule(mode, server), Nothing);

        Assert.Equal(new RuleTarget(TargetKind.Direct, "", TargetReason.NothingUp), target);
    }

    [Theory]
    [InlineData(RuleTargetMode.Direct)]
    [InlineData(RuleTargetMode.Block)]
    public void ServerAskedToLeaveTheTunnel_ReadsAsUnaddressed(RuleTargetMode mode)
    {
        var target = TargetResolver.Resolve(Rule(mode, string.Empty), Fleet);

        Assert.Equal(TargetKind.Auto, target.Kind);
        Assert.Equal(TargetReason.Auto, target.Reason);
    }

    [Theory]
    [InlineData(RouteRole.Direct)]
    [InlineData(RouteRole.Block)]
    public void RuleOutsideTheProxyBucket_IgnoresTheServerFields(RouteRole role)
    {
        var rule = new GeoRule(GeoRuleKind.Cidr, "10.0.0.0/8", role, RuleTargetMode.Server, "nl", RuleTargetMode.Block);

        var target = TargetResolver.Resolve(rule, Fleet);

        Assert.Equal(TargetKind.Auto, target.Kind);
        Assert.Equal(TargetReason.RoleWithoutServer, target.Reason);
    }

    [Fact]
    public void VerdictChangesOnlyWhenTheFleetDoes()
    {
        var rule = SecondChoice("nl", "fi");
        var before = TargetResolver.Resolve(rule, Fleet);

        Assert.Equal(before, TargetResolver.Resolve(rule, new ServerFleet(true, ["fi", "de", "nl"], ["fi", "de"])));
        Assert.NotEqual(before, TargetResolver.Resolve(rule, new ServerFleet(true, ["fi", "de", "nl"], ["fi", "de", "nl"])));
    }

    private static GeoRule Named(string server)
    {
        return Rule(RuleTargetMode.Server, server);
    }

    private static GeoRule SecondChoice(string server, string fallback)
    {
        return Rule(RuleTargetMode.Server, server, RuleTargetMode.Server, fallback);
    }

    private static GeoRule Rule(RuleTargetMode serverMode, string server, RuleTargetMode fallbackMode = RuleTargetMode.Auto, string fallback = "")
    {
        return new GeoRule(GeoRuleKind.Cidr, "1.2.3.0/24", RouteRole.Proxy, serverMode, server, fallbackMode, fallback);
    }
}
