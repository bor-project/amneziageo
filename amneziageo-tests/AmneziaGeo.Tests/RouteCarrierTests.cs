using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Which server carries everything: the one holding it keeps it while its dialling has attempts left, and the
/// next one up the priority takes over once they are spent.
/// </summary>
public sealed class RouteCarrierTests
{
    private static readonly string[] Up = ["fi", "de", "nl"];

    [Fact]
    public void WithNoServerUp_NobodyCarriesAnything()
    {
        Assert.Null(RouteCarrier.Pick([], null, Spent()));
    }

    [Fact]
    public void WithNobodyHoldingIt_TheHeadOfThePriorityCarriesEverything()
    {
        Assert.Equal("fi", RouteCarrier.Pick(Up, null, Spent()));
    }

    [Fact]
    public void WhileTheHolderStillHasDials_EverythingStaysWithIt()
    {
        Assert.Equal("de", RouteCarrier.Pick(Up, "de", Spent()));
    }

    [Fact]
    public void OnceTheHolderRanOutOfDials_EverythingMovesUpThePriority()
    {
        Assert.Equal("fi", RouteCarrier.Pick(Up, "de", Spent("de")));
    }

    [Fact]
    public void HeadThatRanOutOfDials_LeavesTheNextServerCarryingEverything()
    {
        Assert.Equal("de", RouteCarrier.Pick(Up, "fi", Spent("fi")));
    }

    [Fact]
    public void WithEveryServerOutOfDials_EverythingStaysWhereItIs()
    {
        Assert.Equal("de", RouteCarrier.Pick(Up, "de", Spent("fi", "de", "nl")));
    }

    [Fact]
    public void WithEveryServerOutOfDialsAndNobodyHoldingIt_TheHeadCarriesEverything()
    {
        Assert.Equal("fi", RouteCarrier.Pick(Up, null, Spent("fi", "de", "nl")));
    }

    [Fact]
    public void HolderThatIsNoLongerUp_HandsEverythingToTheHead()
    {
        Assert.Equal("fi", RouteCarrier.Pick(Up, "se", Spent()));
    }

    [Fact]
    public void ServerCarryingEverything_HeadsTheServersUp()
    {
        Assert.Equal("nl,fi,de", string.Join(",", RouteCarrier.Head(Up, "nl")));
    }

    [Fact]
    public void ServerAlreadyAtTheHead_LeavesTheOrderAlone()
    {
        Assert.Equal("fi,de,nl", string.Join(",", RouteCarrier.Head(Up, "fi")));
    }

    [Fact]
    public void ServerThatIsNotUp_LeavesTheOrderAlone()
    {
        Assert.Equal("fi,de,nl", string.Join(",", RouteCarrier.Head(Up, "se")));
    }

    private static IReadOnlySet<string> Spent(params string[] names)
    {
        return new HashSet<string>(names, StringComparer.Ordinal);
    }
}
