using AmneziaGeo.Windows.App.Fleet;

using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// A rule by name addressed to another server of a standing set takes effect at once: once a tunnel took its share
/// of the rules up without reconnecting, the tunnels of the set are told to read again which names the others
/// carry, and the one that holds the lookups of the machine does.
/// </summary>
public sealed class LentRereadTests
{
    [Fact]
    public void AfterAShareWasTakenUp_EveryStandingTunnelIsTold()
    {
        Assert.Equal(["first", "second", "third"], FleetHostedService.LentAudience(true, ["first", "second", "third"]));
    }

    [Fact]
    public void APassThatReaddressedNothing_TellsNobody()
    {
        Assert.Empty(FleetHostedService.LentAudience(false, ["first", "second"]));
    }

    [Fact]
    public void ASetOfNone_TellsNobody()
    {
        Assert.Empty(FleetHostedService.LentAudience(true, []));
    }
}
