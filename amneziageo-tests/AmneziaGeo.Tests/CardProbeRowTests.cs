using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The time on the card of a server is the quickest echo of the burst it was measured with: the same reading the
/// running tunnel shows, where the best of the latest echoes stands.
/// </summary>
public sealed class CardProbeRowTests
{
    [Fact]
    public void TheCardOfAServer_ShowsTheQuickestEchoOfTheBurst()
    {
        var leg = new CheckLeg("home", LegState.Ok, RttMs: 48, JitterMs: 6, LossPercent: 0, BestMs: 41);

        var row = CardProbe.Row(new SweepServer("home", "203.0.113.5"), leg);

        Assert.Equal(41, row.RttMs);
        Assert.Equal(6, row.JitterMs);
        Assert.Equal(0, row.LossPercent);
    }

    [Fact]
    public void ALegThatKnowsNoQuickestEcho_KeepsTheTimeItHas()
    {
        var leg = new CheckLeg("home", LegState.Ok, RttMs: 48, JitterMs: 6, LossPercent: 0);

        var row = CardProbe.Row(new SweepServer("home", "203.0.113.5", Live: true), leg);

        Assert.Equal(48, row.RttMs);
        Assert.True(row.Live);
    }
}
