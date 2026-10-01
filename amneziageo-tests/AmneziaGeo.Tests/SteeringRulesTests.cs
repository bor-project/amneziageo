using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A socket that was never connected names its destination in every send, so no table of sockets shows it and no
/// route can be laid for it in time. The ranges of a list are handed to the kernel instead: what the machine opens
/// toward a tunnel range takes the mark of the tunnel with its first packet, and a blocked range is refused there.
/// </summary>
public sealed class SteeringRulesTests
{
    private const string Rule = "    ct direction original ip daddr @proxied ip daddr != @spared meta mark set 0x51820\n";

    [Fact]
    public void Carried_LeavesOutTheDirectAndTheBlockedRanges()
    {
        var carried = SteeringRules.Carried(["198.18.10.0/24"], ["198.18.10.128/25"], ["198.18.10.64/26"]);

        Assert.Equal(["198.18.10.0/26"], carried);
    }

    [Fact]
    public void Carried_IsEmptyForAListWithoutTunnelRanges()
    {
        Assert.Empty(SteeringRules.Carried([], ["198.18.10.0/24"], ["198.18.20.0/24"]));
    }

    [Fact]
    public void Refused_SparesWhatTheConnectionStandsOn()
    {
        var refused = GeoIpRanges.Build(SteeringRules.Refused(
            ["10.224.0.0/24", "198.18.20.0/24", "203.0.113.0/24"],
            ["10.224.0.1", "203.0.113.7", "fd00::1", "10.224.0.8/30"]));

        Assert.False(refused.Contains(Numeric("10.224.0.1")));
        Assert.False(refused.Contains(Numeric("203.0.113.7")));
        Assert.False(refused.Contains(Numeric("10.224.0.9")));
        Assert.True(refused.Contains(Numeric("10.224.0.2")));
        Assert.True(refused.Contains(Numeric("203.0.113.8")));
        Assert.True(refused.Contains(Numeric("198.18.20.5")));
    }

    [Fact]
    public void Marks_ARangeOfTheListTakesTheMarkWhateverTheProtocol()
    {
        var text = SteeringRules.Marks("awg0", null, ["198.18.10.0/25", "203.0.113.0/24"], false, "192.0.2.1");

        Assert.Contains("  set proxied {\n    type ipv4_addr\n    flags interval\n    elements = { 198.18.10.0/25, 203.0.113.0/24 }\n  }\n", text, StringComparison.Ordinal);
        Assert.Contains("  set spared {\n    type ipv4_addr\n  }\n", text, StringComparison.Ordinal);
        Assert.Contains(Rule, text, StringComparison.Ordinal);
        Assert.DoesNotContain("l4proto", text, StringComparison.Ordinal);
        Assert.Contains("    meta mark 0x51820 oifname \"awg0\" masquerade\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Marks_TheLoopbackAndTheServerNeverTakeTheMarkOfARange()
    {
        var text = SteeringRules.Marks("awg0", null, ["0.0.0.0/1"], false, "192.0.2.1");

        var rule = text.IndexOf(Rule, StringComparison.Ordinal);
        Assert.InRange(text.IndexOf("    oifname \"lo\" return\n", StringComparison.Ordinal), 0, rule);
        Assert.InRange(text.IndexOf("    ip daddr 192.0.2.1 return\n", StringComparison.Ordinal), 0, rule);
    }

    [Fact]
    public void Marks_WhatAlreadyLeavesThroughTheTunnelStillTakesTheMarkOfARange()
    {
        var text = SteeringRules.Marks("awg0", null, ["198.18.10.0/25"], false, null);

        Assert.DoesNotContain("oifname \"awg0\" return", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Marks_AServerOverTheSixthFamilyIsNamedInItsFamily()
    {
        var text = SteeringRules.Marks("awg0", null, ["198.18.10.0/25"], false, "2001:db8::1");

        Assert.Contains("    ip6 daddr 2001:db8::1 return\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Marks_AListWithoutRangesStillStandsItsSets()
    {
        var text = SteeringRules.Marks("awg0", "amneziageo", [], false, null);

        Assert.Contains("  set proxied {\n    type ipv4_addr\n    flags interval\n  }\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("elements", text, StringComparison.Ordinal);
        Assert.Contains(Rule, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Marks_AFullTunnelStandsNoSets()
    {
        var text = SteeringRules.Marks("awg0", "amneziageo", null, false, "192.0.2.1");

        Assert.DoesNotContain("set ", text.Replace("mark set", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("@proxied", text, StringComparison.Ordinal);
        Assert.DoesNotContain("return", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Marks_ApplicationsAreMarkedBeforeAnythingIsLetBy()
    {
        var text = SteeringRules.Marks("awg0", "amneziageo", ["198.18.10.0/25"], true, "192.0.2.1");

        var cgroup = text.IndexOf("    socket cgroupv2 level 1 \"amneziageo\" meta mark set 0x51820\n", StringComparison.Ordinal);
        Assert.InRange(cgroup, 0, text.IndexOf("return", StringComparison.Ordinal));
    }

    [Fact]
    public void Marks_EveryDatagramLeavesTheLoopbackTheServerAndTheTunnelAlone()
    {
        var text = SteeringRules.Marks("awg0", null, [], true, "192.0.2.1");

        var chain = string.Join(
            '\n',
            "    type route hook output priority mangle; policy accept;",
            "    oifname \"lo\" return",
            "    ip daddr 192.0.2.1 return",
            Rule.TrimEnd('\n'),
            "    oifname \"awg0\" return",
            "    ip daddr 255.255.255.255 return",
            "    ip daddr 224.0.0.0/4 return",
            "    ip6 daddr ff00::/8 return",
            "    meta l4proto udp meta mark set 0x51820",
            "  }");
        Assert.Contains(chain, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Marks_AnAddressANameHoldsOffTheTunnelIsSparedFromTheStart()
    {
        var text = SteeringRules.Marks("awg0", null, ["198.18.10.0/25"], false, null, ["198.18.10.9"]);

        Assert.Contains("  set spared {\n    type ipv4_addr\n    elements = { 198.18.10.9 }\n  }\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_EmptiesTheSetAndFillsItInOneGo()
    {
        Assert.Equal(
            "flush set inet amneziageo proxied\nadd element inet amneziageo proxied { 198.18.10.0/25, 203.0.113.0/24 }\n",
            SteeringRules.Reload(["198.18.10.0/25", "203.0.113.0/24"], []));
    }

    [Fact]
    public void Reload_OnlyEmptiesTheSetOfAListLeftWithoutRanges()
    {
        Assert.Equal("flush set inet amneziageo proxied\n", SteeringRules.Reload([], []));
    }

    [Fact]
    public void Reload_SparesInTheSameGoWhatANameHoldsOffTheTunnel()
    {
        Assert.Equal(
            "flush set inet amneziageo proxied\nadd element inet amneziageo proxied { 198.18.10.0/24 }\n"
            + "add element inet amneziageo spared { 198.18.10.9, 198.18.10.77 }\n",
            SteeringRules.Reload(["198.18.10.0/24"], ["198.18.10.9", "198.18.10.77"]));
    }

    [Fact]
    public void Bypassed_LeavesOutTheBlockedRangesAndWhatTheConnectionStandsOn()
    {
        var bypassed = GeoIpRanges.Build(SteeringRules.Bypassed(
            ["10.0.0.0/8", "198.18.10.128/25"],
            ["10.1.2.0/24"],
            ["10.224.0.1", "fd00::1", "10.224.0.8/30", "192.0.2.1"]));

        Assert.True(bypassed.Contains(Numeric("10.9.9.9")));
        Assert.True(bypassed.Contains(Numeric("198.18.10.200")));
        Assert.False(bypassed.Contains(Numeric("10.1.2.3")));
        Assert.False(bypassed.Contains(Numeric("10.224.0.1")));
        Assert.False(bypassed.Contains(Numeric("10.224.0.9")));
        Assert.True(bypassed.Contains(Numeric("10.224.0.2")));
    }

    [Fact]
    public void Bypassed_FoldsOverlappingRangesIntoOnes()
    {
        Assert.Equal(["198.18.10.0/24"], SteeringRules.Bypassed(["198.18.10.0/25", "198.18.10.128/25", "198.18.10.64/26"], [], []));
    }

    [Fact]
    public void Bypasses_LayEveryRangeThroughTheHopOfTheMachineInTheTableGiven()
    {
        Assert.Equal(
            "route replace 198.18.10.128/25 via 192.0.2.254 dev eth0 table 51821\n"
            + "route replace 203.0.113.0/24 via 192.0.2.254 dev eth0 table 51821\n",
            SteeringRules.Bypasses(["198.18.10.128/25", "203.0.113.0/24"], "192.0.2.254", "eth0", "51821"));
    }

    [Fact]
    public void Withdrawals_TakeEveryRangeOutOfTheTableGiven()
    {
        Assert.Equal(
            "route del 198.18.10.128/25 table 51821\n",
            SteeringRules.Withdrawals(["198.18.10.128/25"], "51821"));
    }

    [Fact]
    public void Marks_ADirectRangeIsLetByBeforeAnApplicationOrADatagramIsMarked()
    {
        var text = SteeringRules.Marks("awg0", "amneziageo", [], true, "192.0.2.1", null, ["198.18.10.128/25"]);

        Assert.Contains("  set direct {\n    type ipv4_addr\n    flags interval\n    elements = { 198.18.10.128/25 }\n  }\n", text, StringComparison.Ordinal);
        var let = text.IndexOf("    ip daddr @direct return\n", StringComparison.Ordinal);
        Assert.InRange(let, 0, text.IndexOf("    socket cgroupv2 level 1 \"amneziageo\" meta mark set 0x51820\n", StringComparison.Ordinal));
        Assert.InRange(let, 0, text.IndexOf("    meta l4proto udp meta mark set 0x51820\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Marks_AListWithoutDirectRangesStillStandsTheirSetWhereItIsAskedFor()
    {
        var text = SteeringRules.Marks("awg0", "amneziageo", null, false, null, null, []);

        Assert.Contains("  set direct {\n    type ipv4_addr\n    flags interval\n  }\n", text, StringComparison.Ordinal);
        Assert.Contains("    ip daddr @direct return\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Marks_AnAddressANameKeepsDirectIsLetByBeforeAnApplicationOrADatagramIsMarked()
    {
        var text = SteeringRules.Marks("awg0", "amneziageo", [], true, "192.0.2.1", ["198.18.30.6"], []);

        Assert.Contains("  set spared {\n    type ipv4_addr\n    elements = { 198.18.30.6 }\n  }\n", text, StringComparison.Ordinal);
        var let = text.IndexOf("    ip daddr @spared return\n", StringComparison.Ordinal);
        Assert.InRange(let, 0, text.IndexOf("    socket cgroupv2 level 1 \"amneziageo\" meta mark set 0x51820\n", StringComparison.Ordinal));
        Assert.InRange(let, 0, text.IndexOf("    meta l4proto udp meta mark set 0x51820\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Marks_ATunnelThatCarriesEverythingStandsTheSparedAddressesForItsApplications()
    {
        var text = SteeringRules.Marks("awg0", "amneziageo", null, false, null, null, []);

        Assert.Contains("  set spared {\n    type ipv4_addr\n  }\n", text, StringComparison.Ordinal);
        Assert.Contains("    ip daddr @spared return\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("@proxied", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Marks_WhereOnlyTheRangesAreMarkedNoAddressIsLetByAhead()
    {
        var text = SteeringRules.Marks("awg0", null, ["198.18.10.0/25"], false, null, ["198.18.10.9"]);

        Assert.DoesNotContain("@spared return", text, StringComparison.Ordinal);
        Assert.Contains(Rule, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Marks_WhereTheDirectRangesAreNotAskedForNoSetOfThemStands()
    {
        var text = SteeringRules.Marks("awg0", null, ["198.18.10.0/25"], false, null);

        Assert.DoesNotContain("set direct", text, StringComparison.Ordinal);
        Assert.DoesNotContain("@direct", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_FillsTheDirectRangesInTheSameGo()
    {
        Assert.Equal(
            "flush set inet amneziageo proxied\nadd element inet amneziageo proxied { 198.18.10.0/25 }\n"
            + "flush set inet amneziageo direct\nadd element inet amneziageo direct { 198.18.10.128/25 }\n",
            SteeringRules.Reload(["198.18.10.0/25"], [], ["198.18.10.128/25"]));
    }

    [Fact]
    public void Reload_LeavesAloneTheSetsTheTableDoesNotStand()
    {
        Assert.Equal("flush set inet amneziageo direct\n", SteeringRules.Reload(null, ["198.18.10.9"], []));
    }

    [Fact]
    public void Refusals_ABlockedRangeIsRefusedWhateverTheProtocolApartFromTheLoopback()
    {
        var text = SteeringRules.Refusals(["198.18.20.0/24"]);

        Assert.StartsWith("table inet amneziageo-block\ndelete table inet amneziageo-block\ntable inet amneziageo-block {\n", text, StringComparison.Ordinal);
        Assert.Contains("    elements = { 198.18.20.0/24 }\n", text, StringComparison.Ordinal);
        var refuse = text.IndexOf("    ip daddr @blocked reject with icmpx type admin-prohibited\n", StringComparison.Ordinal);
        Assert.InRange(text.IndexOf("    oifname \"lo\" accept\n", StringComparison.Ordinal), 0, refuse);
        Assert.DoesNotContain("l4proto", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Leading_ADirectRangeStandingInItsTableIsNamedByThatTable()
    {
        Assert.Equal("51821", SteeringRules.Leading(
            "198.18.10.203 via 10.223.1.1 dev eth1 table 51821 src 10.223.1.2 uid 0 \n    cache \n", "awg0"));
    }

    [Fact]
    public void Leading_ANetworkTheMachineStandsInIsNamedByTheMainTable()
    {
        Assert.Equal(SteeringRules.MainTable, SteeringRules.Leading("10.223.9.2 dev eth2 src 10.223.9.1 uid 0 \n    cache \n", "awg0"));
    }

    [Fact]
    public void Leading_ARouteOfTheMachineThroughAHopIsNamedByTheMainTable()
    {
        Assert.Equal("main", SteeringRules.Leading("10.50.1.1 via 192.0.2.254 dev eth0 src 192.0.2.7 uid 0 \n    cache \n", "awg0"));
    }

    [Fact]
    public void Leading_AnAddressTheTunnelTakesIsLedByNothing()
    {
        Assert.Null(SteeringRules.Leading("198.18.30.6 dev awg0 src 10.224.0.2 uid 0 \n    cache \n", "awg0"));
    }

    [Fact]
    public void Leading_AnInterfaceWhoseNameOnlyStartsLikeTheTunnelIsNotTheTunnel()
    {
        Assert.Equal("main", SteeringRules.Leading("10.9.0.2 dev awg01 src 10.9.0.1 uid 0 \n", "awg0"));
    }

    [Fact]
    public void Leading_AnAddressOfTheMachineItselfIsNamedByTheLocalTable()
    {
        Assert.Equal("local", SteeringRules.Leading("local 10.223.1.2 dev lo table local src 10.223.1.2 uid 0 \n    cache <local> \n", "awg0"));
    }

    [Fact]
    public void Leading_AnAnswerWithoutAnInterfaceLeadsNowhere()
    {
        Assert.Null(SteeringRules.Leading("RTNETLINK answers: Network is unreachable\n", "awg0"));
        Assert.Null(SteeringRules.Leading(string.Empty, "awg0"));
    }

    private static uint Numeric(string address)
    {
        Assert.True(GeoIpRanges.TryToNumeric(System.Net.IPAddress.Parse(address), out var value));

        return value;
    }
}
