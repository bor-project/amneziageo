using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The rules of a list split between the route table and the relay in the order of the list: a rule heavier than half
/// the threshold is decided on the fly however light the rest is, and the rest is held while the threshold lasts.
/// </summary>
public sealed class SplitShareTests
{
    [Fact]
    public void ALightList_IsHeldWhole()
    {
        var share = SplitBudget.Share([10, 1_424, 1], 11_000);

        Assert.Equal([true, true, true], share.Held);
        Assert.Equal(1_435, share.HeldWeight);
        Assert.Equal(0, share.LiveWeight);
        Assert.False(share.AnyLive);
    }

    [Fact]
    public void ARuleHeavierThanHalfTheThreshold_IsDecidedOnTheFly()
    {
        var share = SplitBudget.Share([8_500, 1_424], 11_000);

        Assert.Equal([false, true], share.Held);
        Assert.Equal(1_424, share.HeldWeight);
        Assert.Equal(8_500, share.LiveWeight);
        Assert.True(share.AnyLive);
    }

    [Fact]
    public void ARuleOfHalfTheThreshold_IsHeld()
    {
        var share = SplitBudget.Share([5_500, 5_500], 11_000);

        Assert.Equal([true, true], share.Held);
        Assert.False(share.AnyLive);
    }

    [Fact]
    public void WhatNoLongerFits_IsDecidedOnTheFlyAndALighterRuleAfterItIsHeld()
    {
        var share = SplitBudget.Share([5_000, 5_000, 2_000, 900], 11_000);

        Assert.Equal([true, true, false, true], share.Held);
        Assert.Equal(10_900, share.HeldWeight);
        Assert.Equal(2_000, share.LiveWeight);
    }

    [Fact]
    public void ARuleOfNoWeight_IsHeldBesideAHeavyOne()
    {
        var share = SplitBudget.Share([25_166, 0], 11_000);

        Assert.Equal([false, true], share.Held);
        Assert.Equal(25_166, share.LiveWeight);
    }

    [Fact]
    public void TheHalf_FollowsTheThresholdOfTheSettings()
    {
        Assert.True(SplitBudget.Heavy(8_001, 16_000));
        Assert.False(SplitBudget.Heavy(8_000, 16_000));
        Assert.True(SplitBudget.Heavy(5_501, 11_000));
    }

    [Fact]
    public void AnEmptyList_HoldsNothingAndDecidesNothingOnTheFly()
    {
        var share = SplitBudget.Share([], 11_000);

        Assert.Empty(share.Held);
        Assert.False(share.AnyLive);
    }
}
