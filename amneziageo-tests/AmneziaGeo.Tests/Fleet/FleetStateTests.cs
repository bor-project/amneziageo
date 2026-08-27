using AmneziaGeo.Ipc.Fleet;
using Xunit;

namespace AmneziaGeo.Tests.Fleet;

/// <summary>
/// The mode reads its own state back at every start, so what it writes has to survive the round trip: the
/// order of the servers, the role each holds and the set that was up.
/// </summary>
public sealed class FleetStateTests
{
    [Fact]
    public void AStoredSetReadsBackAsItWasWritten()
    {
        var roles = new Dictionary<string, string> { ["alpha"] = TunnelRoles.Primary, ["bravo"] = TunnelRoles.Neutral };

        Assert.Equal(["alpha", "bravo"], FleetState.ParseNames(FleetState.FormatNames(["alpha", "bravo"])));
        Assert.Equal(roles, FleetState.ParseRoles(FleetState.FormatRoles(roles)));
    }

    [Fact]
    public void AnEmptyStoreReadsAsAModeNeverEntered()
    {
        Assert.Empty(FleetState.ParseNames(null));
        Assert.Empty(FleetState.ParseNames(string.Empty));
        Assert.Empty(FleetState.ParseRoles(null));
        Assert.Empty(FleetState.Empty.Order);
        Assert.Equal(string.Empty, FleetState.Empty.Primary);
    }

    [Fact]
    public void BlankLinesAndRepeatsAreDropped()
    {
        Assert.Equal(["alpha", "bravo"], FleetState.ParseNames("alpha\n\n  bravo  \nalpha\n"));
        Assert.Equal("alpha\nbravo", FleetState.FormatNames(["alpha", " ", "bravo", "alpha"]));
    }

    [Fact]
    public void ARoleNoOneKnowsIsIgnored()
    {
        var roles = FleetState.ParseRoles("alpha=primary\nbravo=captain\ncharlie=\n=neutral");

        Assert.Equal(TunnelRoles.Primary, roles["alpha"]);
        Assert.Single(roles);
    }

    [Fact]
    public void ANameCarryingAnEqualsSignKeepsIt()
    {
        var roles = FleetState.ParseRoles(FleetState.FormatRoles(new Dictionary<string, string> { ["a=b"] = TunnelRoles.Neutral }));

        Assert.Equal(TunnelRoles.Neutral, roles["a=b"]);
    }
}
