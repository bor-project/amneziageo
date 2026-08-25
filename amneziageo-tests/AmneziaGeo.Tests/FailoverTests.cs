using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Auto-switching: the servers it walks, the ones it passes over, the place a picked server takes and when the
/// default route moves.
/// </summary>
public sealed class FailoverTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    [Fact]
    public void Names_ReadOneToALineWithoutTheBlanks()
    {
        Assert.Equal(["a", "b"], NameList.Split("a\r\n\r\n  b  \n"));
        Assert.Empty(NameList.Split(null));
    }

    [Fact]
    public void Names_PruneDropsWhatNoConfigAnswersTo()
    {
        Assert.Equal("b", NameList.Prune("a\nb", ["b", "c"]));
    }

    [Fact]
    public void Names_PruneKeepsTheOrderItWasGivenIn()
    {
        Assert.Equal(NameList.Join(["b", "a"]), NameList.Prune("b\na", ["a", "b"]));
    }

    [Fact]
    public void Order_RaisesThePickedServerAndKeepsTheRestBehindIt()
    {
        Assert.Equal(["b", "a", "c"], FailoverPolicy.Raise(["a", "b", "c"], "b"));
        Assert.Equal(["c", "a", "b"], FailoverPolicy.Raise(["a", "b", "c"], "c"));
    }

    [Fact]
    public void Order_StandsWhenThePickedServerAlreadyHeadsIt()
    {
        Assert.Equal(["a", "b"], FailoverPolicy.Raise(["a", "b"], "a"));
    }

    [Fact]
    public void Order_StandsWhenNoServerAnswersToThePick()
    {
        Assert.Equal(["a", "b"], FailoverPolicy.Raise(["a", "b"], "z"));
        Assert.Equal(["a", "b"], FailoverPolicy.Raise(["a", "b"], "A"));
    }

    [Fact]
    public void Walk_PassesOverTheServersLeftOutAndKeepsTheOrderOfTheRest()
    {
        Assert.Equal(["a", "c"], FailoverPolicy.Participants(["a", "b", "c"], "b"));
        Assert.Equal(["a", "b"], FailoverPolicy.Participants(["a", "b"], null));
    }

    [Fact]
    public void Walk_TakesTheServerTheDecisionNames()
    {
        Assert.Equal("b", FailoverPolicy.Walk(["a", "b", "c"], "a", "b", []));
    }

    [Fact]
    public void Walk_PassesOverTheServersAlreadyDialledInThisSearch()
    {
        Assert.Equal("c", FailoverPolicy.Walk(["a", "b", "c"], "b", "a", ["a"]));
    }

    [Fact]
    public void Walk_NeverNamesTheServerCarryingTheRouteNow()
    {
        Assert.Equal("b", FailoverPolicy.Walk(["a", "b", "c"], "c", "a", ["a"]));
    }

    [Fact]
    public void Walk_SaysTheLapIsDoneWhenEveryServerHasBeenDialled()
    {
        Assert.Equal(string.Empty, FailoverPolicy.Walk(["a", "b", "c"], "c", "a", ["a", "b", "c"]));
    }

    [Fact]
    public void Above_NamesTheServersStandingHigherInTheOrderTheyStandIn()
    {
        Assert.Equal(["a", "b"], FailoverPolicy.Above(["a", "b", "c"], "c"));
        Assert.Empty(FailoverPolicy.Above(["a", "b", "c"], "a"));
    }

    [Fact]
    public void Above_LeavesNothingAboveAServerTheListDoesNotCarry()
    {
        Assert.Empty(FailoverPolicy.Above(["a", "b"], "z"));
    }

    [Fact]
    public void Reserves_KeepTheServersTheRouteCanGoBackTo()
    {
        Assert.Equal(["a", "b"], FailoverPolicy.Reserves(["a", "b", "c"], "c", true, new FailoverSettings(true, 5)));
    }

    [Fact]
    public void Reserves_StandDownWhileTheRouteIsNotComingBack()
    {
        Assert.Empty(FailoverPolicy.Reserves(["a", "b", "c"], "c", true, new FailoverSettings(true, 0)));
        Assert.Empty(FailoverPolicy.Reserves(["a", "b", "c"], "c", true, new FailoverSettings(false, 5)));
    }

    [Fact]
    public void Reserves_StandDownWhileTheServerCarryingTheRouteAnswersNothing()
    {
        Assert.Empty(FailoverPolicy.Reserves(["a", "b", "c"], "c", false, new FailoverSettings(true, 5)));
    }

    [Fact]
    public void Overlap_NamesTwoServersHandingOutTheSameSubnet()
    {
        Assert.True(TunnelOverlap.Same(["10.8.1.2/32"], ["10.8.1.3/32"]));
        Assert.True(TunnelOverlap.Same(["10.8.1.2/24"], ["10.8.1.2/32"]));
    }

    [Fact]
    public void Overlap_LeavesServersInSubnetsOfTheirOwnAlone()
    {
        Assert.False(TunnelOverlap.Same(["10.8.1.2/32"], ["10.9.1.2/32"]));
    }

    [Fact]
    public void Overlap_SaysNothingWhereThereIsNoAddressToMeasureBy()
    {
        Assert.False(TunnelOverlap.Same([], ["10.8.1.2/32"]));
        Assert.False(TunnelOverlap.Same(["fd00::2/64"], ["fd00::3/64"]));
    }

    [Fact]
    public void Decide_LeavesTheServerThatStoppedAnsweringOnTheThirdReading()
    {
        var policy = new FailoverPolicy();
        var at = Origin;

        Assert.Equal(FailoverDecision.Stay, Round(policy, [Dying("a"), Healthy("b")], "a", at));
        Assert.Equal(FailoverDecision.Stay, Round(policy, [Dying("a"), Healthy("b")], "a", at += Step));
        Assert.Equal(FailoverDecision.SwitchTo("b"), Round(policy, [Dying("a"), Healthy("b")], "a", at + Step));
    }

    [Fact]
    public void Decide_CountsAHolderWithNoTunnelUnderItAsFallen()
    {
        var policy = new FailoverPolicy();
        var at = Origin;

        for (var round = 0; round < FailoverPolicy.FallSamples - 1; round++)
        {
            Round(policy, [Down("a"), Healthy("b")], "a", at += Step);
        }

        Assert.Equal(FailoverDecision.SwitchTo("b"), Round(policy, [Down("a"), Healthy("b")], "a", at + Step));
    }

    [Fact]
    public void Decide_StaysWhenEveryRaisedServerWentBadAtOnce()
    {
        var policy = new FailoverPolicy();
        var at = Origin;

        for (var round = 0; round < FailoverPolicy.FallSamples + 1; round++)
        {
            Assert.Equal(FailoverDecision.Stay, Round(policy, [Dying("a"), Dying("b")], "a", at += Step));
        }
    }

    [Fact]
    public void Decide_StaysWhenTheFallenServerHasNowhereToHandTheRouteTo()
    {
        var policy = new FailoverPolicy();
        var at = Origin;

        for (var round = 0; round < FailoverPolicy.FallSamples + 1; round++)
        {
            Assert.Equal(FailoverDecision.Stay, Round(policy, [Dying("a")], "a", at += Step));
        }
    }

    [Fact]
    public void Decide_TakesALossShareNobodyMeasuredForNoComplaint()
    {
        var policy = new FailoverPolicy();
        var unmeasured = new FailoverReading("a", true, new LinkReading(1_000_000, 200_000, 0, LinkHealth.LossUnknown, 30));
        var at = Origin;

        for (var round = 0; round < FailoverPolicy.FallSamples + 1; round++)
        {
            Assert.Equal(FailoverDecision.Stay, Round(policy, [unmeasured, Healthy("b")], "a", at += Step));
        }
    }

    [Fact]
    public void Decide_CarriesTheRouteBackOnceThePriorityServerAnsweredAndTheTunnelWentQuiet()
    {
        var policy = new FailoverPolicy();
        Round(policy, [Silent("a"), Silent("b")], "b", Origin, returnMinutes: 5);

        Assert.Equal(
            FailoverDecision.ReturnTo("a"),
            Round(policy, [Silent("a"), Silent("b")], "b", Origin.AddMinutes(5), returnMinutes: 5));
    }

    [Fact]
    public void Decide_KeepsTheRouteWhileTheTunnelIsCarryingTraffic()
    {
        var policy = new FailoverPolicy();
        Round(policy, [Silent("a"), Healthy("b")], "b", Origin, returnMinutes: 5);

        Assert.Equal(
            FailoverDecision.Stay,
            Round(policy, [Silent("a"), Healthy("b")], "b", Origin.AddMinutes(5), returnMinutes: 5));
    }

    [Fact]
    public void Decide_KeepsTheRouteUntilThePriorityServerHasAnsweredLongEnough()
    {
        var policy = new FailoverPolicy();
        Round(policy, [Silent("a"), Silent("b")], "b", Origin, returnMinutes: 5);

        Assert.Equal(
            FailoverDecision.Stay,
            Round(policy, [Silent("a"), Silent("b")], "b", Origin.AddMinutes(1), returnMinutes: 5));
    }

    [Fact]
    public void Decide_KeepsTheRouteWhereItIsWhenTheReturnIsSwitchedOff()
    {
        var policy = new FailoverPolicy();
        Round(policy, [Silent("a"), Silent("b")], "b", Origin);

        Assert.Equal(FailoverDecision.Stay, Round(policy, [Silent("a"), Silent("b")], "b", Origin.AddHours(1)));
    }

    [Fact]
    public void Decide_DoesNotSendTheRouteStraightBackToTheServerThatFell()
    {
        var policy = new FailoverPolicy();
        var at = Origin;
        for (var round = 0; round < FailoverPolicy.FallSamples - 1; round++)
        {
            Round(policy, [Dying("a"), Silent("b")], "a", at += Step, returnMinutes: 5);
        }

        Assert.Equal(
            FailoverDecision.SwitchTo("b"),
            Round(policy, [Dying("a"), Silent("b")], "a", at += Step, returnMinutes: 5));

        // The server is back on its feet, but the route stays until it has stood for the whole wait.
        Assert.Equal(FailoverDecision.Stay, Round(policy, [Silent("a"), Silent("b")], "b", at += Step, returnMinutes: 5));
        Assert.Equal(FailoverDecision.Stay, Round(policy, [Silent("a"), Silent("b")], "b", at + Step, returnMinutes: 5));
    }

    [Fact]
    public void Decide_StaysOutOfItWhileAutoSwitchingIsOff()
    {
        var policy = new FailoverPolicy();
        var at = Origin;

        for (var round = 0; round < FailoverPolicy.FallSamples + 1; round++)
        {
            var decision = policy.Decide([Dying("a"), Healthy("b")], new FailoverSettings(false, 5), "a", at += Step);
            Assert.Equal(FailoverDecision.Stay, decision);
        }
    }

    private static FailoverDecision Round(FailoverPolicy policy, IReadOnlyList<FailoverReading> readings, string holder, DateTimeOffset at, int returnMinutes = 0)
    {
        return policy.Decide(readings, new FailoverSettings(true, returnMinutes), holder, at);
    }

    private static FailoverReading Healthy(string name)
    {
        return new FailoverReading(name, true, new LinkReading(1_000_000, 200_000, 0, 0, 30));
    }

    private static FailoverReading Silent(string name)
    {
        return new FailoverReading(name, true, new LinkReading(0, 0, 0, 0, 30));
    }

    // Nothing arrives while the session is re-established over and over: the far end is gone.
    private static FailoverReading Dying(string name)
    {
        return new FailoverReading(name, true, new LinkReading(0, 400_000, LinkHealth.ChurnPerMinute + 3, LinkHealth.LossUnknown, -1));
    }

    private static FailoverReading Down(string name)
    {
        return new FailoverReading(name, false, LinkReading.Empty);
    }
}
