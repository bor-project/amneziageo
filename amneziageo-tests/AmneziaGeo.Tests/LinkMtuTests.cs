using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A link size that was read is kept until the network changes; where the platform does not tell of a change it is
/// kept for a minute.
/// </summary>
public sealed class LinkMtuTests
{
    [Fact]
    public void WhereTheNetworkTellsOfAChange_AReadingStaysAsLongAsItLikes()
    {
        Assert.True(LinkMtu.Fresh(100_000, 100_000 + 3_600_000, timed: false));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(59_999, true)]
    [InlineData(60_000, false)]
    [InlineData(3_600_000, false)]
    public void WhereItDoesNot_AReadingLivesAMinute(long age, bool fresh)
    {
        Assert.Equal(fresh, LinkMtu.Fresh(100_000, 100_000 + age, timed: true));
    }
}
