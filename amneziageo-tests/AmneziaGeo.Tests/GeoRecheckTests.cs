using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// When a scheduled check of the rule databases asks again the sources it did not reach.
/// </summary>
public sealed class GeoRecheckTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 25, 8, 50, 0, TimeSpan.Zero);
    private static readonly TimeSpan _pause = TimeSpan.FromMinutes(10);

    [Fact]
    public void ASourceTheCheckMissed_IsAskedAgainAfterThePause()
    {
        var recheck = new GeoRecheck(_pause, 3);
        recheck.Took(["zkeenip"], _start, whole: true);

        Assert.Empty(recheck.Due(_start + _pause - TimeSpan.FromSeconds(1)));
        Assert.Equal(["zkeenip"], recheck.Due(_start + _pause));
    }

    [Fact]
    public void ASourceThatAnsweredAgain_IsNotAskedAnyMore()
    {
        var recheck = new GeoRecheck(_pause, 3);
        recheck.Took(["zkeenip", "geoip"], _start, whole: true);
        recheck.Took(["geoip"], _start + _pause, whole: false);

        Assert.Equal(["geoip"], recheck.Due(_start + (_pause * 2)));
    }

    [Fact]
    public void TheTries_RunOut()
    {
        var recheck = new GeoRecheck(_pause, 2);
        recheck.Took(["zkeenip"], _start, whole: true);
        recheck.Took(["zkeenip"], _start + _pause, whole: false);
        recheck.Took(["zkeenip"], _start + (_pause * 2), whole: false);

        Assert.Empty(recheck.Due(_start + (_pause * 10)));
    }

    [Fact]
    public void AWholeCheck_StartsTheTriesOver()
    {
        var recheck = new GeoRecheck(_pause, 1);
        recheck.Took(["zkeenip"], _start, whole: true);
        recheck.Took(["zkeenip"], _start + _pause, whole: false);
        recheck.Took(["zkeenip"], _start + (_pause * 3), whole: true);

        Assert.Equal(["zkeenip"], recheck.Due(_start + (_pause * 4)));
    }
}
