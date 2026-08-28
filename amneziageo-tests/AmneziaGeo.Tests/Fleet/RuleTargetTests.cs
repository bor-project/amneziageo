using AmneziaGeo.Ipc.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// Where a rule rides is read back at every start, so both ends of it have to survive the round trip - and a
/// rule left to the machine has to take no room at all, or a mode nobody addressed a rule in would store a line
/// per rule.
/// </summary>
public sealed class RuleTargetTests
{
    [Theory]
    [InlineData("", RuleTarget.Auto, "")]
    [InlineData("auto", RuleTarget.Auto, "")]
    [InlineData("  AUTO ", RuleTarget.Auto, "")]
    [InlineData("best", RuleTarget.Best, "")]
    [InlineData("block", RuleTarget.Block, "")]
    [InlineData("bor-dev-b", RuleTarget.Server, "bor-dev-b")]
    public void ReadsOneEnd(string text, string mode, string name)
    {
        var target = RuleTarget.Parse(text);

        Assert.Equal(mode, target.Mode);
        Assert.Equal(name, target.Name);
    }

    [Fact]
    public void AnEndWritesBackAsItWasRead()
    {
        foreach (var word in new[] { "auto", "best", "block", "bor-dev-b" })
        {
            Assert.Equal(word, RuleTarget.Parse(word).Format());
        }
    }

    [Fact]
    public void AValueNamingOneEndLeavesTheOtherToTheMachine()
    {
        var route = RuleRoute.Parse("bravo");

        Assert.Equal("bravo", route.Target.Name);
        Assert.True(route.Fallback.IsAuto);
    }

    [Fact]
    public void AnAddressedRuleReadsBackAsItWasWritten()
    {
        var targets = new Dictionary<string, RuleRoute>
        {
            [FleetTargets.Key(3, "geoip:us")] = new(new RuleTarget(RuleTarget.Server, "bravo"), new RuleTarget(RuleTarget.Block)),
            [FleetTargets.Key(3, "app:path=C:\\apps\\x.exe")] = new(new RuleTarget(RuleTarget.Best), RuleTarget.Default),
        };

        var read = FleetTargets.Parse(FleetTargets.Format(targets));

        Assert.Equal(targets, read);
    }

    [Fact]
    public void AKeyNamesItsListAndItsRule()
    {
        Assert.True(FleetTargets.TrySplit(FleetTargets.Key(6, "geosite:youtube"), out var listId, out var token));
        Assert.Equal(6, listId);
        Assert.Equal("geosite:youtube", token);

        Assert.False(FleetTargets.TrySplit("geosite:youtube", out _, out _));
        Assert.False(FleetTargets.TrySplit("6:", out _, out _));
    }

    [Fact]
    public void ARuleByNameTakesNoAddress()
    {
        Assert.True(RuleAddressing.ByName("geosite:youtube"));
        Assert.True(RuleAddressing.ByName(" DOMAIN:printer.local "));
        Assert.False(RuleAddressing.ByName("geoip:ru"));
        Assert.False(RuleAddressing.ByName("cidr:10.0.0.0/8"));
        Assert.False(RuleAddressing.ByName("app:path=C:\\apps\\x.exe"));

        // An address stored on one before it was refused is dropped rather than read back and never applied.
        var read = FleetTargets.Parse("6:geosite:rutracker=bravo,direct\n6:geoip:ru=bravo");

        Assert.Single(read);
        Assert.True(read.ContainsKey(FleetTargets.Key(6, "geoip:ru")));
    }

    [Fact]
    public void ARuleLeftToTheMachineIsNotStored()
    {
        var targets = new Dictionary<string, RuleRoute> { [FleetTargets.Key(1, "geoip:ru")] = RuleRoute.Default };

        Assert.Equal(string.Empty, FleetTargets.Format(targets));
        Assert.Empty(FleetTargets.Parse("1:geoip:ru=auto,auto"));
        Assert.Empty(FleetTargets.Parse(null));
    }
}
