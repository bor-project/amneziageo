using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The layout the distributor came to, as the diagnostics read it: where every rule was sent, what led there, and
/// what each server was left carrying.
/// </summary>
public sealed class RoutingLayoutTests : IAsyncLifetime
{
    // One range no rule addresses anywhere, one a rule sends to a server by name.
    private const string Unaddressed = "10.1.0.0/16";
    private const string Addressed = "10.2.0.0/16";

    private MachineHarness _machine = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _machine = await MachineHarness.StartAsync();
        await _machine.LibraryAsync("fi", "de", "nl");
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _machine.DisposeAsync();
    }

    [Fact]
    public async Task RuleNamingAServerThatIsUp_SaysItRidesThatServer()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi", "de");
        await _machine.DistributeAsync();

        var layout = await _machine.LayoutAsync();

        Assert.True(layout.MultiServer);
        Assert.Equal("main", layout.List);
        var row = Row(layout, Addressed);
        Assert.Equal("server", row.Kind);
        Assert.Equal("de", row.Server);
        Assert.Equal("Named", row.Reason);
    }

    [Fact]
    public async Task RuleNamingNobody_SaysItRidesWhoeverCarriesEverything()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi", "de");
        await _machine.DistributeAsync();

        var row = Row(await _machine.LayoutAsync(), Unaddressed);

        Assert.Equal("auto", row.Kind);
        Assert.Equal("fi", row.Server);
        Assert.Equal("Auto", row.Reason);
    }

    [Fact]
    public async Task EveryServerUp_IsCountedByWhatItWasDealt()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi", "de");
        await _machine.DistributeAsync();

        var layout = await _machine.LayoutAsync();

        Assert.Equal(["fi", "de"], layout.Servers.Select(server => server.Server));
        Assert.Equal([1, 1], layout.Servers.Select(server => server.Rules));
        Assert.Equal([true, false], layout.Servers.Select(server => server.Carrier));
        Assert.All(layout.Servers, server => Assert.Equal("main", server.List));
    }

    [Fact]
    public async Task RuleWhoseServerIsDown_SaysWhichWayItFell()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Named(Addressed, "de", RuleTargetMode.Block));
        _machine.Raise("fi");
        await _machine.DistributeAsync();

        var layout = await _machine.LayoutAsync();

        var row = Row(layout, Addressed);
        Assert.Equal("block", row.Kind);
        Assert.Equal(string.Empty, row.Server);
        Assert.Equal("FallbackBlocked", row.Reason);
        Assert.Equal(1, layout.Blocked);
    }

    [Fact]
    public async Task RuleNamingAServerTheLibraryDoesNotHold_SaysSo()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Named(Addressed, "se"));
        _machine.Raise("fi");
        await _machine.DistributeAsync();

        var row = Row(await _machine.LayoutAsync(), Addressed);

        Assert.Equal("auto", row.Kind);
        Assert.Equal("UnknownServer", row.Reason);
    }

    [Fact]
    public async Task WithNothingUp_EveryRuleGoesPastTheTunnel()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));

        var layout = await _machine.LayoutAsync();

        Assert.Empty(layout.Servers);
        Assert.Equal(2, layout.Direct);
        Assert.All(layout.Rules, row => Assert.Equal("direct", row.Kind));
        Assert.All(layout.Rules, row => Assert.Equal("NothingUp", row.Reason));
    }

    [Fact]
    public async Task ReadingTheLayout_SettlesNothingAndHandsOverNothing()
    {
        await _machine.ModeOnAsync();
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi", "de");

        var layout = await _machine.LayoutAsync();

        Assert.Equal("fi", layout.Servers[0].Server);
        Assert.Null(_machine.Control.DefaultRouteOwner);
        Assert.Empty(await _machine.CarriedAsync("fi"));
        Assert.Empty(await _machine.CarriedAsync("de"));
    }

    [Fact]
    public async Task WithSeveralServersOff_EachTunnelNamesTheWholeListItIsBoundTo()
    {
        await _machine.ListAsync(Rule(Unaddressed), Named(Addressed, "de"));
        _machine.Raise("fi", "de");
        await _machine.DistributeAsync();

        var layout = await _machine.LayoutAsync();

        Assert.False(layout.MultiServer);
        Assert.Empty(layout.Rules);
        Assert.Equal(["fi", "de"], layout.Servers.Select(server => server.Server));
        Assert.Equal([2, 2], layout.Servers.Select(server => server.Rules));
        Assert.All(layout.Servers, server => Assert.Equal("main", server.List));
    }

    private static RuleLayout Row(RoutingLayout layout, string cidr)
    {
        return layout.Rules.Single(row => row.Rule.Contains(cidr, StringComparison.Ordinal));
    }

    private static GeoRule Rule(string cidr)
    {
        return new GeoRule(GeoRuleKind.Cidr, cidr);
    }

    private static GeoRule Named(string cidr, string server, RuleTargetMode fallbackMode = RuleTargetMode.Auto)
    {
        return new GeoRule(GeoRuleKind.Cidr, cidr, RouteRole.Proxy, RuleTargetMode.Server, server, fallbackMode);
    }
}
