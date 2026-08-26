using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Which server carries everything the rules do not name, and which cards the machine keeps up: the priority
/// settles it before anything is dialled, and only a primary that ran out of dials gives it up.
/// </summary>
public sealed class PrimaryServerTests : IAsyncLifetime
{
    private MachineHarness _machine = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _machine = await MachineHarness.StartAsync();
        await _machine.ModeOnAsync();
        await _machine.LibraryAsync("fi", "de", "nl");
        await _machine.ListAsync(new GeoRule(GeoRuleKind.Cidr, "10.2.0.0/16", RouteRole.Proxy, RuleTargetMode.Server, "nl"));
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _machine.DisposeAsync();
    }

    [Fact]
    public async Task CardRaisedWithNothingUp_CarriesEverything()
    {
        var role = await _machine.DistributeAsync(raising: "de");

        Assert.Equal("de", _machine.Control.DefaultRouteOwner);
        Assert.False(role.Split);
        Assert.True(role.Preferred);
    }

    [Fact]
    public async Task ReserveComingUpFirst_LeavesTheDefaultRouteWithThePrimary()
    {
        // The whole set is asked for at once and the primary takes the route before anything is dialled.
        _machine.Raise("fi", "de");
        _machine.Control.ClaimDefaultRoute("fi", preferred: true);

        var role = await _machine.DistributeAsync(raising: "de");

        Assert.Equal("fi", _machine.Control.DefaultRouteOwner);
        Assert.True(role.Split);
        Assert.False(role.Preferred);
    }

    [Fact]
    public async Task PrimaryWithDialsLeft_KeepsEverything()
    {
        _machine.Raise("fi", "de");
        await _machine.DistributeAsync();

        _machine.Dial("fi", ConnectDial.Attempts - 1);
        await _machine.DistributeAsync();

        Assert.Equal("fi", _machine.Control.DefaultRouteOwner);
    }

    [Fact]
    public async Task PrimaryOutOfDials_LeavesTheNextServerCarryingEverything()
    {
        _machine.Raise("fi", "de");
        await _machine.DistributeAsync();

        _machine.Dial("fi", ConnectDial.Attempts);
        await _machine.DistributeAsync();

        Assert.Equal("de", _machine.Control.DefaultRouteOwner);
        Assert.True(_machine.Control.IsRunning("fi"));
    }

    [Fact]
    public async Task PrimaryAnsweredAtLast_KeepsEverythingAndItsDialsBack()
    {
        _machine.Raise("fi", "de");
        await _machine.DistributeAsync();

        _machine.Dial("fi", ConnectDial.Attempts);
        _machine.Answer("fi");
        await _machine.DistributeAsync();

        Assert.Equal("fi", _machine.Control.DefaultRouteOwner);
        Assert.Equal(0, _machine.Control.For("fi", _machine.UserRoot).RetryAttempt);
    }

    [Fact]
    public async Task PrimaryTakenDown_LeavesTheNextServerCarryingEverything()
    {
        _machine.Raise("fi", "de");
        await _machine.DistributeAsync();

        _machine.Drop("fi");
        await _machine.DistributeAsync();

        Assert.Equal("de", _machine.Control.DefaultRouteOwner);
    }

    [Fact]
    public async Task SetOfCardsSurvivesARestart()
    {
        await _machine.SwitchOffAsync("de");

        Assert.Equal(["fi", "nl"], await _machine.RosterAsync());

        await _machine.RestartAsync();

        Assert.Equal(["fi", "nl"], await _machine.RosterAsync());
    }

    [Fact]
    public async Task TakingEverythingDown_LeavesTheCardsAsTheyWere()
    {
        await _machine.SwitchOffAsync("de");
        await _machine.Store.SetSettingAsync(StateKeys.VpnOff, "1");

        Assert.Empty(await _machine.RosterAsync());

        await _machine.Store.SetSettingAsync(StateKeys.VpnOff, string.Empty);

        Assert.Equal(["fi", "nl"], await _machine.RosterAsync());
    }
}
