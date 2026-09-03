using AmneziaGeo.Ipc.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// The role decides what the balancer may do with a tunnel, so an unreadable token must land on the role
/// that changes nothing rather than on the one that owns the default route.
/// </summary>
public sealed class TunnelRolesTests
{
    [Theory]
    [InlineData("primary", "primary")]
    [InlineData("reserve", "reserve")]
    [InlineData("neutral", "neutral")]
    [InlineData("PRIMARY", "primary")]
    [InlineData("  neutral  ", "neutral")]
    public void ReadsKnownTokens(string text, string expected)
    {
        Assert.Equal(expected, TunnelRoles.Of(text));
        Assert.True(TunnelRoles.IsKnown(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("backup")]
    [InlineData(null)]
    public void FallsBackToReserve(string? text)
    {
        Assert.Equal(TunnelRoles.Reserve, TunnelRoles.Of(text));
        Assert.False(TunnelRoles.IsKnown(text));
    }

    [Theory]
    [InlineData(0, "neutral")]
    [InlineData(-1, "neutral")]
    [InlineData(1, "primary")]
    [InlineData(2, "reserve")]
    [InlineData(7, "reserve")]
    public void ThePlaceGivesTheRole(int slot, string expected)
    {
        Assert.Equal(expected, TunnelRoles.At(slot));
    }

    [Fact]
    public void BalancerLeavesNeutralAlone()
    {
        Assert.True(TunnelRoles.Balanced(TunnelRoles.Primary));
        Assert.True(TunnelRoles.Balanced(TunnelRoles.Reserve));
        Assert.False(TunnelRoles.Balanced(TunnelRoles.Neutral));
    }
}
