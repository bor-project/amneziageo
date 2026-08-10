using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The loss share the home screen shows: what the window counts, when it refuses to answer, and which addresses
/// it is allowed to echo. A share taken too early, taken against a target that answers nothing, or taken past the
/// server that carries it would read as a broken link - the one thing this number must never invent.
/// </summary>
public sealed class LinkLossProbeTests
{
    [Fact]
    public void BeforeAnythingIsMeasured_TheShareIsUnknown()
    {
        var probe = new LinkLossProbe(["10.0.0.1"]);

        Assert.Equal(LinkHealth.LossUnknown, probe.Percent);
    }

    [Fact]
    public void FewerAttemptsThanTheFloor_LeaveTheShareUnknown()
    {
        var probe = new LinkLossProbe(["10.0.0.1"]);

        Record(probe, 9, false);

        Assert.Equal(LinkHealth.LossUnknown, probe.Percent);
    }

    [Fact]
    public void ThreeAttemptsOfTenLost_ReadAsThirtyPercent()
    {
        var probe = new LinkLossProbe(["10.0.0.1"]);

        Record(probe, 7, true);
        Record(probe, 3, false);

        Assert.Equal(30, probe.Percent);
    }

    [Fact]
    public void ALinkThatRecovers_LosesItsOldMissesOutOfTheWindow()
    {
        var probe = new LinkLossProbe(["10.0.0.1"]);

        Record(probe, 30, false);
        Assert.Equal(100, probe.Percent);

        Record(probe, 30, true);

        Assert.Equal(0, probe.Percent);
    }

    [Fact]
    public void AStoppedTunnel_LeavesNoHistoryBehind()
    {
        var probe = new LinkLossProbe(["10.0.0.1"]);
        Record(probe, 10, false);

        probe.Reset();

        Assert.Equal(LinkHealth.LossUnknown, probe.Percent);
    }

    [Fact]
    public void TheChannelIsEchoedAtThePeerAlone()
    {
        var targets = LinkLossProbe.PeerTargets(["10.0.0.6/24"]);

        Assert.Equal(["10.0.0.1"], targets);
    }

    [Fact]
    public void TheResolversTheConfigDeclares_BelongToThePathPastTheExit()
    {
        Assert.Equal(["1.1.1.1", "9.9.9.9"], LinkLossProbe.BeyondTargets(["1.1.1.1", "9.9.9.9"]));
        Assert.Empty(LinkLossProbe.PeerTargets([]));
    }

    [Fact]
    public void ASingleHostAddress_IsReadAsOneSubnetOfTheServer()
    {
        var targets = LinkLossProbe.PeerTargets(["10.8.2.21/32"]);

        Assert.Equal(["10.8.2.1"], targets);
    }

    [Fact]
    public void AnAddressAlreadyTheFirstHost_NamesNoPeer()
    {
        var targets = LinkLossProbe.PeerTargets(["10.8.2.1/24"]);

        Assert.Empty(targets);
    }

    [Fact]
    public void AddressesTheEchoCannotUse_AreLeftOut()
    {
        Assert.Equal(["1.1.1.1"], LinkLossProbe.BeyondTargets(["fd00::1", "1.1.1.1", "1.1.1.1"]));
        Assert.Empty(LinkLossProbe.PeerTargets(["fdcc::cafe/128"]));
    }

    [Fact]
    public void TheChangedShare_IsWorthASnapshot()
    {
        var clean = new LinkReading(1000, 1000, 0, 0);

        Assert.True(clean.DiffersFrom(clean with { LossPercent = 12 }));
    }

    private static void Record(LinkLossProbe probe, int times, bool answered)
    {
        for (var i = 0; i < times; i++)
        {
            probe.Record(answered);
        }
    }
}
