using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Windows.App.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// The header is the switch of the whole machine: it takes every tunnel of the set down at once, and brings
/// back the ones that stood rather than one of them. What stood has to outlive the set being down, and the
/// mode standing back up, or the machine comes back on less than it had.
/// </summary>
public sealed class FleetSwitchTests
{
    [Fact]
    public void TakingTheSetDownRemembersWhatStood()
    {
        var fleet = Standing("alpha", "bravo");

        Assert.True(fleet.TakeAllDown());

        Assert.Empty(fleet.Wanted);
        Assert.Equal(["alpha", "bravo"], fleet.Resume);
    }

    [Fact]
    public void ASetAlreadyDownHasNothingToTake()
    {
        var fleet = new FleetControl(new FleetLive());

        Assert.False(fleet.TakeAllDown());
        Assert.Empty(fleet.Resume);
    }

    [Fact]
    public void TheSetComesBackOnEverythingItRemembered()
    {
        var fleet = Standing("alpha", "bravo");
        fleet.TakeAllDown();

        var raised = fleet.BringBack("charlie");

        Assert.Equal(["alpha", "bravo"], raised);
        Assert.Equal(["alpha", "bravo"], fleet.Wanted);
        Assert.Empty(fleet.Resume);
    }

    [Fact]
    public void WithNothingRememberedTheSetStandsOnTheNameGiven()
    {
        var fleet = new FleetControl(new FleetLive());

        var raised = fleet.BringBack("alpha");

        Assert.Equal(["alpha"], raised);
        Assert.Equal(["alpha"], fleet.Wanted);
    }

    [Fact]
    public void WithNothingRememberedAndNothingNamedNothingComesUp()
    {
        var fleet = new FleetControl(new FleetLive());

        Assert.Empty(fleet.BringBack(string.Empty));
        Assert.Empty(fleet.Wanted);
    }

    [Fact]
    public void WhatStoodOutlivesTheModeStandingBackUp()
    {
        var fleet = Standing("alpha", "bravo");
        fleet.TakeAllDown();

        var stood = new FleetControl(new FleetLive());
        stood.Restore(fleet.Snapshot());

        Assert.Empty(stood.Wanted);
        Assert.Equal(["alpha", "bravo"], stood.Resume);
        Assert.Equal(["alpha", "bravo"], stood.BringBack(string.Empty));
    }

    [Fact]
    public void AStruckServerLeavesWhatIsRemembered()
    {
        var fleet = Standing("alpha", "bravo");
        fleet.TakeAllDown();

        Assert.True(fleet.Forget("alpha"));

        Assert.Equal(["bravo"], fleet.Resume);
    }

    [Fact]
    public void ARenamedServerIsRememberedUnderTheNewName()
    {
        var fleet = Standing("alpha", "bravo");
        fleet.TakeAllDown();

        Assert.True(fleet.Rename("bravo", "delta"));

        Assert.Equal(["alpha", "delta"], fleet.Resume);
    }

    // A set standing on the named servers.
    private static FleetControl Standing(params string[] names)
    {
        var fleet = new FleetControl(new FleetLive());
        foreach (var name in names)
        {
            fleet.Add(name);
        }

        return fleet;
    }
}
