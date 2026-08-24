using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Several tunnels at once: the set the agent keeps up, who carries the default route, and what a tunnel that
/// does not carry it is left with.
/// </summary>
public sealed class MultiTunnelTests
{
    [Fact]
    public void Control_KeepsOneEntryPerConfig()
    {
        var control = new AgentControl();

        var first = control.For("a");
        var again = control.For("a");
        var second = control.For("b");

        Assert.Same(first, again);
        Assert.NotSame(first, second);
        Assert.Equal(2, control.Tunnels.Count);
    }

    [Fact]
    public void Control_ListsOnlyTheTunnelsThatAreUp()
    {
        var control = new AgentControl();
        control.For("a").SetRunning(true);
        control.For("b");

        Assert.True(control.Running);
        Assert.Equal(["a"], control.Desired.Select(tunnel => tunnel.Config));
        Assert.True(control.IsRunning("a"));
        Assert.False(control.IsRunning("b"));
    }

    [Fact]
    public void Control_HoldsSeveralTunnelsUpAtOnce()
    {
        var control = new AgentControl();
        control.For("a").SetRunning(true);
        control.For("b").SetRunning(true);

        Assert.Equal(["a", "b"], control.Desired.Select(tunnel => tunnel.Config).Order());
        Assert.Equal("a", control.RunningTarget);
    }

    [Fact]
    public void Control_FollowsARenameWithoutDroppingTheTunnel()
    {
        var control = new AgentControl();
        control.SetTarget("a");
        control.For("a").SetRunning(true);

        control.RetargetName("a", "b");

        Assert.Equal("b", control.Target);
        Assert.Null(control.Find("a"));
        Assert.True(control.IsRunning("b"));
    }

    [Fact]
    public void Control_ForgetsATornDownTunnelButKeepsALatchedFailure()
    {
        var control = new AgentControl();
        var quiet = control.For("a");
        quiet.SetRunning(true);
        quiet.SetRunning(false);
        var failed = control.For("b");
        failed.SetRunning(true);
        failed.FailConnect(ConnectFailureReason.NoHandshake, "silent");

        control.Forget("a");
        control.Forget("b");

        Assert.Null(control.Find("a"));
        Assert.NotNull(control.Find("b"));
        Assert.True(control.Find("b")!.ConnectFailed);
    }

    [Fact]
    public void DefaultRoute_GoesToTheFirstClaimAndTheRestCarryTheirOwnRanges()
    {
        var control = new AgentControl();
        control.For("a").SetRunning(true);
        control.For("b").SetRunning(true);

        var first = control.ClaimDefaultRoute("a", preferred: false);
        var second = control.ClaimDefaultRoute("b", preferred: false);

        Assert.True(first.Granted);
        Assert.False(second.Granted);
        Assert.Null(second.Displaced);
        Assert.Equal("a", control.DefaultRouteOwner);
    }

    [Fact]
    public void DefaultRoute_TakenByThePickedConfigNamesWhoGivesItUp()
    {
        var control = new AgentControl();
        var held = control.For("a");
        held.SetRunning(true);
        control.For("b").SetRunning(true);
        control.ClaimDefaultRoute("a", preferred: false);

        var taken = control.ClaimDefaultRoute("b", preferred: true);

        Assert.True(taken.Granted);
        Assert.Same(held, taken.Displaced);
        Assert.Equal("b", control.DefaultRouteOwner);
    }

    [Fact]
    public void DefaultRoute_ClaimedTwiceByItsHolderStaysWhereItIs()
    {
        var control = new AgentControl();
        control.For("a").SetRunning(true);
        control.ClaimDefaultRoute("a", preferred: false);

        var again = control.ClaimDefaultRoute("a", preferred: false);

        Assert.True(again.Granted);
        Assert.Null(again.Displaced);
    }

    [Fact]
    public void DefaultRoute_IsFreeAgainOnceItsTunnelGoesDown()
    {
        var control = new AgentControl();
        control.For("a").SetRunning(true);
        control.ClaimDefaultRoute("a", preferred: false);

        control.ReleaseDefaultRoute("b");
        Assert.Equal("a", control.DefaultRouteOwner);

        control.ReleaseDefaultRoute("a");
        Assert.Null(control.DefaultRouteOwner);
    }

    [Fact]
    public void Resolver_GoesToTheFirstTunnelUp()
    {
        var control = new AgentControl();
        control.For("a").SetRunning(true);
        control.For("b").SetRunning(true);

        Assert.True(control.ClaimResolver("a").Granted);
        Assert.False(control.ClaimResolver("b").Granted);
        Assert.Equal("a", control.ResolverOwner);
    }

    [Fact]
    public void Resolver_FollowsTheTunnelThatCarriesTheDefaultRoute()
    {
        var control = new AgentControl();
        var first = control.For("a");
        first.SetRunning(true);
        control.For("b").SetRunning(true);
        control.ClaimResolver("a");
        control.ClaimDefaultRoute("b", preferred: false);

        var taken = control.ClaimResolver("b");

        Assert.True(taken.Granted);
        Assert.Same(first, taken.Displaced);
        Assert.Equal("b", control.ResolverOwner);
    }

    [Fact]
    public void Resolver_IsFreeAgainOnceItsTunnelGoesDown()
    {
        var control = new AgentControl();
        control.For("a").SetRunning(true);
        control.ClaimResolver("a");

        control.ReleaseResolver("b");
        Assert.Equal("a", control.ResolverOwner);

        control.ReleaseResolver("a");
        Assert.Null(control.ResolverOwner);
    }

    [Fact]
    public void Tunnel_BelongsToTheUserThatRaisedIt()
    {
        var control = new AgentControl();
        var tunnel = control.For("a", @"C:\users\one", "S-1-5-21-1");

        Assert.True(tunnel.IsOwnedBy(@"C:\users\one", "S-1-5-21-1"));
        Assert.False(tunnel.IsOwnedBy(@"C:\users\two", "S-1-5-21-2"));

        // Without a SID on either side the data root decides, trailing separator and all.
        Assert.True(control.For("b", @"C:\users\one").IsOwnedBy(@"C:\users\one\", null));
    }

    [Fact]
    public void AllowedIps_WithoutDefaultsKeepsOnlyTheNamedRanges()
    {
        IReadOnlyList<string> allowed = ["0.0.0.0/0", "::/0", "10.8.0.0/24", "192.168.1.0/24"];

        Assert.Equal(["10.8.0.0/24", "192.168.1.0/24"], AllowedIpsResolver.WithoutDefaults(allowed));
    }

    [Fact]
    public void AllowedIps_WithoutDefaultsDropsTheHalvesTheEngineSplitThemInto()
    {
        IReadOnlyList<string> allowed = ["0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1", "10.8.0.0/24"];

        Assert.Equal(["10.8.0.0/24"], AllowedIpsResolver.WithoutDefaults(allowed));
    }

    [Fact]
    public void AllowedIps_FullTunnelFallsBackToEverythingWhenTheConfigNamesNothing()
    {
        Assert.Equal(["0.0.0.0/0", "::/0"], AllowedIpsResolver.Build(false, [], []));
    }
}
