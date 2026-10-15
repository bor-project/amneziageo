using AmneziaGeo.Windows.App;
using AmneziaGeo.Windows.App.Fleet;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Turning several servers at once on or off changes the machine over when asked, and brings up only what stood.
/// </summary>
public sealed class ModeSwitchTests
{
    [Fact]
    public void TheSingleTunnelComesBackWhenTheSetStoodOnIt()
    {
        Assert.True(ModeSwitchService.SoleComesBack(wanted: false, soleWasUp: true, setStands: true));
    }

    [Fact]
    public void ASetTakenDownLeavesTheMachineDownWhenTheModeGoesOff()
    {
        Assert.False(ModeSwitchService.SoleComesBack(wanted: false, soleWasUp: true, setStands: false));
    }

    [Fact]
    public void TheSingleTunnelStaysDownWhenItWasNotUpOrTheModeGoesOn()
    {
        Assert.False(ModeSwitchService.SoleComesBack(wanted: false, soleWasUp: false, setStands: true));
        Assert.False(ModeSwitchService.SoleComesBack(wanted: true, soleWasUp: true, setStands: true));
    }

    [Fact]
    public async Task AWakeEndsTheWaitBetweenReadsOfTheFlag()
    {
        var mode = new AgentMode();
        var started = Environment.TickCount64;

        var nap = mode.NapAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        mode.Wake();
        await nap;

        Assert.True(Environment.TickCount64 - started < 5_000);
    }

    [Fact]
    public async Task TheMachineSettlesOnTheModeItWasTurnedTo()
    {
        var mode = new AgentMode();
        mode.Stood();
        var turn = Task.Run(async () =>
        {
            await mode.NapAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            mode.Leaving();
            mode.MultiServer = true;
            mode.Stood();
        });

        var settled = await mode.SettleAsync(true, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(settled);
        await turn;
    }

    [Fact]
    public async Task ASupervisorThatHasNotStoodYetIsNotSettled()
    {
        var mode = new AgentMode
        {
            MultiServer = true,
        };
        mode.Leaving();

        Assert.False(await mode.SettleAsync(true, TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }

    [Fact]
    public void TheSingleTunnelDialsOnAStartOnlyWhenTheFlagDidNotMove()
    {
        Assert.True(AgentBackgroundService.DialsOnStart(surviveReboot: true, switched: false));
        Assert.False(AgentBackgroundService.DialsOnStart(surviveReboot: true, switched: true));
        Assert.False(AgentBackgroundService.DialsOnStart(surviveReboot: false, switched: false));
    }

    [Fact]
    public async Task AMachineThatDoesNotChangeOverInTimeIsNotSettled()
    {
        var mode = new AgentMode();
        mode.Stood();

        var settled = await mode.SettleAsync(true, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.False(settled);
        Assert.True(await mode.SettleAsync(false, TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }
}
