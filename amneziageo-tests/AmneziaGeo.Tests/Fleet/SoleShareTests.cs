using AmneziaGeo.Decl;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// С одним туннелем адрес правила скрыт, но действует: правило, названное другой конфигурацией, идёт по запасному.
/// </summary>
public sealed class SoleShareTests
{
    private const long List = 1;

    private static readonly GeoRule Home = new(GeoRuleKind.Cidr, "192.168.1.0/24");

    private static IReadOnlyDictionary<string, RuleRoute> Addressed(string target, string fallback)
    {
        return new Dictionary<string, RuleRoute>
        {
            [FleetTargets.Key(List, "cidr:192.168.1.0/24")] = new(RuleTarget.Parse(target), RuleTarget.Parse(fallback)),
        };
    }

    [Fact]
    public void ARuleAddressedToTheRunningTunnel_RidesIt()
    {
        IReadOnlyList<GeoRule> rules = [Home];
        Assert.Same(rules, TunnelDutyRoster.Alone("home", List, rules, Addressed("home", "direct")));
    }

    [Fact]
    public void ARuleAddressedToAnotherConfiguration_TakesItsFallback()
    {
        Assert.Equal([Home with { Role = RouteRole.Direct }], TunnelDutyRoster.Alone("fi", List, [Home], Addressed("home", "direct")));
        Assert.Equal([Home with { Role = RouteRole.Block }], TunnelDutyRoster.Alone("fi", List, [Home], Addressed("home", "block")));
        Assert.Equal([Home], TunnelDutyRoster.Alone("fi", List, [Home], Addressed("home", "fi")));
        Assert.Equal([Home], TunnelDutyRoster.Alone("fi", List, [Home], Addressed("home", "auto")));
    }

    [Fact]
    public void ARuleWhoseEndsNameOtherConfigurations_LeavesTheTunnel()
    {
        Assert.Empty(TunnelDutyRoster.Alone("fi", List, [Home], Addressed("home", "work")));
    }

    [Fact]
    public void AnUnaddressedRuleAndTheOtherBuckets_StayAsTheyAre()
    {
        IReadOnlyList<GeoRule> rules = [new(GeoRuleKind.GeoSite, "github"), Home with { Role = RouteRole.Direct }];
        Assert.Same(rules, TunnelDutyRoster.Alone("fi", List, rules, Addressed("home", "direct")));
        Assert.Same(rules, TunnelDutyRoster.Alone("fi", List, rules, new Dictionary<string, RuleRoute>()));
    }
}
