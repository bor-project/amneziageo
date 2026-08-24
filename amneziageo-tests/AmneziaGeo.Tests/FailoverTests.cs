using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Auto-switching: the servers it walks, the ones it passes over and the place a picked server takes.
/// </summary>
public sealed class FailoverTests
{
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
}
