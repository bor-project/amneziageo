using AmneziaGeo.Decl;
using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Which configurations the machine keeps up: the server carrying everything plus the ones rules name, less the
/// cards switched off.
/// </summary>
public sealed class ServerRosterTests
{
    private static readonly string[] Order = ["fi", "de", "nl"];

    [Fact]
    public void HeadOfThePriority_StandsWhetherARuleNamesItOrNot()
    {
        Assert.Equal("fi", Roster([]));
    }

    [Fact]
    public void ServerARuleNames_Stands()
    {
        Assert.Equal("fi,nl", Roster([Rule("netflix", "nl")]));
    }

    [Fact]
    public void ServerNoRuleNames_StaysDown()
    {
        Assert.Equal("fi,de", Roster([Rule("netflix", "de")]));
    }

    [Fact]
    public void SecondChoiceARuleNames_StandsBeside()
    {
        Assert.Equal("fi,de,nl", Roster([Rule("netflix", "de", RuleTargetMode.Server, "nl")]));
    }

    [Fact]
    public void CardSwitchedOff_StaysDownEvenWhenARuleNamesIt()
    {
        Assert.Equal("fi", Roster([Rule("netflix", "nl")], "nl"));
    }

    [Fact]
    public void HeadSwitchedOff_LeavesTheNextCardCarryingEverything()
    {
        Assert.Equal("de", Roster([], "fi"));
    }

    [Fact]
    public void EveryCardSwitchedOff_LeavesNobodyUp()
    {
        Assert.Equal(string.Empty, Roster([], "fi", "de", "nl"));
    }

    [Fact]
    public void RuleOutsideTheProxyBucket_NamesNoServer()
    {
        var direct = new GeoRule(GeoRuleKind.GeoIp, "ru", RouteRole.Direct, RuleTargetMode.Server, "nl");

        Assert.Equal("fi", Roster([direct]));
    }

    [Fact]
    public void RuleAddressingNoServerByName_RaisesNobodyBesides()
    {
        var best = new GeoRule(GeoRuleKind.GeoSite, "netflix", RouteRole.Proxy, RuleTargetMode.Best);

        Assert.Equal("fi", Roster([best]));
    }

    [Fact]
    public void ServerTheLibraryDoesNotHold_RaisesNobody()
    {
        Assert.Equal("fi", Roster([Rule("netflix", "se")]));
    }

    [Fact]
    public void WithEverythingTakenDownByHand_NobodyStands()
    {
        Assert.Equal(string.Empty, Join(ServerRoster.Build(true, true, Order, [Rule("netflix", "nl")], [], "fi")));
    }

    [Fact]
    public void WithSeveralServersOff_ThePickedConfigurationStandsAlone()
    {
        Assert.Equal("de", Join(ServerRoster.Build(false, false, Order, [Rule("netflix", "nl")], [], "de")));
    }

    [Fact]
    public void WithSeveralServersOffAndNothingPicked_NobodyStands()
    {
        Assert.Equal(string.Empty, Join(ServerRoster.Build(false, false, Order, [], [], null)));
    }

    private static string Roster(IReadOnlyList<GeoRule> rules, params string[] disabled)
    {
        return Join(ServerRoster.Build(true, false, Order, rules, disabled, null));
    }

    private static GeoRule Rule(string value, string server, RuleTargetMode fallbackMode = RuleTargetMode.Auto, string fallback = "")
    {
        return new GeoRule(GeoRuleKind.GeoSite, value, RouteRole.Proxy, RuleTargetMode.Server, server, fallbackMode, fallback);
    }

    private static string Join(IReadOnlyList<string> roster)
    {
        return string.Join(",", roster);
    }
}
