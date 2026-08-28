using AmneziaGeo.Windows.App.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// What the mode stands up on when the flag moves under a running machine. The first time in it remembers no
/// set of its own, and a machine standing on a tunnel must not be stood down by the flag alone.
/// </summary>
public sealed class FleetRestoreTests
{
    [Fact]
    public void AModeRememberingNoSetStandsUpOnTheTunnelCarryingTheMachine()
    {
        Assert.Equal("alpha", Assert.Single(FleetHostedService.StandOn([], "alpha", true)));

        // What the mode remembers is what it stands up on; the machine's tunnel is not added to it.
        Assert.Equal("bravo", Assert.Single(FleetHostedService.StandOn(["bravo"], "alpha", true)));

        // Nothing carried the machine when the flag moved, so the mode stands up on nothing.
        Assert.Empty(FleetHostedService.StandOn([], "alpha", false));
        Assert.Empty(FleetHostedService.StandOn([], string.Empty, true));
    }
}
