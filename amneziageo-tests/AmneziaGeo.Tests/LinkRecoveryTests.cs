using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// When a live tunnel counts as no longer carrying and what is tried about it. The reading that must never happen
/// here is a broken link called healthy because the server still answers its handshakes - and the one that must
/// never happen either is a tunnel nobody is using torn down because nothing came back through it.
/// </summary>
public sealed class LinkRecoveryTests
{
    private const int Unknown = LinkHealth.LossUnknown;

    private static readonly RecoveryStep[] _ladder =
        [RecoveryStep.Rebind, RecoveryStep.Resolve, RecoveryStep.Carrier, RecoveryStep.Restart];

    // Carrying: traffic both ways, one rekey every few minutes, every echo answered.
    private static readonly LinkSample _carrying = new(true, true, 0, 0, 20);

    // Nobody using the tunnel, and no echo has ever been answered to measure it by.
    private static readonly LinkSample _idle = new(true, false, Unknown, 0, 20);

    // Every echo lost: the link is measurable and measures as gone.
    private static readonly LinkSample _silent = new(true, false, 100, 0, 20);

    [Fact]
    public void ALinkThatKeepsReceiving_IsNeverRepaired()
    {
        var recovery = Ladder();

        var steps = Feed(recovery, 60, _carrying);

        Assert.Empty(steps);
        Assert.False(recovery.Repairing);
    }

    [Fact]
    public void ATunnelNobodyIsUsing_IsNotTakenForADeadOne()
    {
        var recovery = Ladder();

        var steps = Feed(recovery, 300, _idle);

        Assert.Empty(steps);
        Assert.False(recovery.Repairing);
    }

    [Fact]
    public void ASessionBeingReestablished_IsRepairedThoughItsHandshakeStaysYoung()
    {
        var recovery = Ladder();

        var steps = Feed(recovery, LinkRecovery.DeadSeconds + 1, _carrying with { HandshakesPerMinute = LinkHealth.ChurnPerMinute });

        Assert.Equal([RecoveryStep.Rebind], steps);
        Assert.Contains("re-established", recovery.Reason);
    }

    [Fact]
    public void EchoesThatAllStopReturning_NameTheLinkDead()
    {
        var recovery = Ladder();

        var steps = Feed(recovery, LinkRecovery.DeadSeconds + 1, _silent);

        Assert.Equal([RecoveryStep.Rebind], steps);
        Assert.Contains("echo", recovery.Reason);
    }

    [Fact]
    public void ATunnelSendingIntoSilenceWithAnAgeingHandshake_IsRepaired()
    {
        var recovery = Ladder();

        var steps = Feed(recovery, LinkRecovery.DeadSeconds + 1, _idle with { HandshakeAgeSeconds = LinkRecovery.DefaultDeadHandshakeSeconds });

        Assert.Equal([RecoveryStep.Rebind], steps);
        Assert.Contains("nothing comes back", recovery.Reason);
    }

    [Fact]
    public void ALinkStillDeadAfterARepair_ClimbsToTheNextRung()
    {
        var recovery = Ladder();

        var steps = Feed(recovery, 120, _silent);

        Assert.Equal([RecoveryStep.Rebind, RecoveryStep.Resolve, RecoveryStep.Carrier, RecoveryStep.Restart], [.. steps[..4]]);
    }

    [Fact]
    public void TheTopRung_IsServedForEveryAttemptPastIt()
    {
        var recovery = Ladder();

        var steps = Feed(recovery, 300, _silent);

        Assert.All(steps[3..], step => Assert.Equal(RecoveryStep.Restart, step));
    }

    [Fact]
    public void EachAttempt_WaitsLongerThanTheOneBefore()
    {
        var recovery = Ladder();

        var at = Times(recovery, 300, _silent);

        Assert.True(at[2] - at[1] > at[1] - at[0]);
        Assert.True(at[3] - at[2] > at[2] - at[1]);
    }

    [Fact]
    public void OnePacketArrivingMidRepair_DoesNotClearTheLadder()
    {
        var recovery = Ladder();
        var now = Stall(recovery);

        recovery.Sample(_carrying, now += 1000);
        recovery.Sample(_silent, now + 1000);

        Assert.True(recovery.Repairing);
    }

    [Fact]
    public void TrafficThatKeepsArriving_StandsTheLadderDown()
    {
        var recovery = Ladder();
        var now = Stall(recovery);

        for (var second = 0; second <= LinkRecovery.HealthySeconds; second++)
        {
            recovery.Sample(_carrying, now += 1000);
        }

        Assert.False(recovery.Repairing);
        Assert.Equal(0, recovery.Attempt);
        Assert.Equal(string.Empty, recovery.Reason);
    }

    [Fact]
    public void ALinkRepairedAndBrokenAgain_StartsFromTheCheapestRung()
    {
        var recovery = Ladder();
        var now = Stall(recovery);
        for (var second = 0; second <= LinkRecovery.HealthySeconds; second++)
        {
            recovery.Sample(_carrying, now += 1000);
        }

        var step = default(RecoveryStep?);
        for (var second = 0; second <= LinkRecovery.DeadSeconds && step is null; second++)
        {
            step = recovery.Sample(_silent, now += 1000);
        }

        Assert.Equal(RecoveryStep.Rebind, step);
    }

    [Fact]
    public void ALadderThatNeverHelps_StandsDownAndSaysWhy()
    {
        var recovery = Ladder();

        var steps = Feed(recovery, LinkRecovery.GiveUpSeconds + 120, _silent);

        Assert.True(recovery.GivenUp);
        Assert.NotEmpty(recovery.Reason);
        Assert.NotEmpty(steps);
        Assert.Null(recovery.Sample(_silent, (LinkRecovery.GiveUpSeconds + 200) * 1000L));
    }

    [Fact]
    public void ARepairAHostCannotPerform_IsNeverAskedFor()
    {
        var recovery = new LinkRecovery([RecoveryStep.Restart], jitterPercent: 0);

        var steps = Feed(recovery, 120, _silent);

        Assert.NotEmpty(steps);
        Assert.All(steps, step => Assert.Equal(RecoveryStep.Restart, step));
    }

    private static LinkRecovery Ladder()
    {
        return new LinkRecovery(_ladder, jitterPercent: 0);
    }

    // Runs the link dead and returns the moment the first repair was asked for.
    private static long Stall(LinkRecovery recovery)
    {
        var now = 0L;
        for (var second = 0; second <= LinkRecovery.DeadSeconds; second++)
        {
            now += 1000;
            if (recovery.Sample(_silent, now) is not null)
            {
                break;
            }
        }

        return now;
    }

    private static List<RecoveryStep> Feed(LinkRecovery recovery, int seconds, LinkSample sample)
    {
        var steps = new List<RecoveryStep>();
        for (var second = 1; second <= seconds; second++)
        {
            if (recovery.Sample(sample, second * 1000L) is { } step)
            {
                steps.Add(step);
            }
        }

        return steps;
    }

    private static List<long> Times(LinkRecovery recovery, int seconds, LinkSample sample)
    {
        var at = new List<long>();
        for (var second = 1; second <= seconds; second++)
        {
            if (recovery.Sample(sample, second * 1000L) is not null)
            {
                at.Add(second);
            }
        }

        return at;
    }
}
