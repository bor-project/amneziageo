using AmneziaGeo.Ipc;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The journal exists because a list swapped behind the user's back was found by hand. A line that does not name
/// both sides, or that is written when nothing moved, would not have helped.
/// </summary>
public sealed class SwitchLogTests
{
    [Fact]
    public void AChangedServer_NamesBothSides()
    {
        Assert.Equal("active server: \"myvpn\" (was \"bor\")", SwitchLog.Config("bor", "myvpn"));
    }

    [Fact]
    public void AFirstSelection_SaysThereWasNone()
    {
        Assert.Equal("active server: \"bor\" (was none)", SwitchLog.Config(null, "bor"));
        Assert.Equal("active server: none (was \"bor\")", SwitchLog.Config("bor", null));
    }

    [Fact]
    public void AChangedRoutingList_NamesBothSides()
    {
        Assert.Equal("routing list: \"teat\" (was \"main\")", SwitchLog.RoutingList("main", "teat"));
        Assert.Equal("routing list: none (was \"main\")", SwitchLog.RoutingList("main", null));
    }

    [Fact]
    public void ASelectionThatDidNotMove_WritesNothing()
    {
        Assert.Null(SwitchLog.Config("bor", "bor"));
        Assert.Null(SwitchLog.Config(null, null));
        Assert.Null(SwitchLog.RoutingList("main", "main"));
        Assert.Null(SwitchLog.RoutingList(string.Empty, null));
    }
}
