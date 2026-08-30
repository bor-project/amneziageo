using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A tun on android is handed its whole route table at establish() and cannot be edited afterwards, so a rule set
/// wider than that table has to be cut down to one. What is cut out still has to be what covers the most addresses,
/// and what the tun must keep - a blocked range, the network the device sits on - has to survive the cut.
/// </summary>
public sealed class SystemRoutesTests
{
    private static int RouteCount(IReadOnlyList<string> carved, IReadOnlyList<string> keep, IReadOnlyList<string> block)
    {
        var direct = new List<string>(carved);
        direct.AddRange(keep);
        return SystemRoutes.Tunneled(true, [], direct, block).Count;
    }

    [Fact]
    public void Carve_TakesTheWidestRangeWhenTheBudgetHoldsOne()
    {
        var carved = SystemRoutes.Carve(["192.168.7.0/24", "10.0.0.0/8", "172.16.0.0/12"], [], [], 8);

        Assert.Equal(["10.0.0.0/8"], carved);
    }

    [Fact]
    public void Carve_TakesEveryRangeWhenTheBudgetHoldsThemAll()
    {
        var carved = SystemRoutes.Carve(["192.168.7.0/24", "10.0.0.0/8", "172.16.0.0/12"], [], [], 1000);

        Assert.Contains("10.0.0.0/8", carved);
        Assert.Contains("172.16.0.0/12", carved);
        Assert.Contains("192.168.7.0/24", carved);
    }

    // Ranges a page apart, so neither the set nor the gaps between its members fold into one route.
    private static List<string> Scattered(int head, int count)
    {
        var ranges = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            ranges.Add($"{head}.{i / 128}.{i % 128 * 2}.0/24");
        }

        return ranges;
    }

    [Fact]
    public void Carve_StaysWithinTheBudgetOnALongList()
    {
        var carved = SystemRoutes.Carve(Scattered(10, 400), [], [], 100);

        Assert.NotEmpty(carved);
        Assert.True(RouteCount(carved, [], []) <= 100);
    }

    [Fact]
    public void Carve_CountsTheRangesTheTunKeepsOutAnyway()
    {
        var direct = Scattered(10, 400);
        var keep = Scattered(172, 40);

        var alone = SystemRoutes.Carve(direct, [], [], 100);
        var beside = SystemRoutes.Carve(direct, keep, [], 100);

        Assert.True(beside.Count < alone.Count);
        Assert.True(RouteCount(beside, keep, []) <= 100);
    }

    [Fact]
    public void Carve_LeavesABlockedRangeInsideTheTun()
    {
        var carved = SystemRoutes.Carve(["10.0.0.0/8"], [], ["10.1.0.0/16"], 1000);

        Assert.Equal(["10.0.0.0/8"], carved);
        Assert.Contains("10.1.0.0/16", SystemRoutes.Tunneled(true, [], carved, ["10.1.0.0/16"]));
    }

    [Fact]
    public void Carve_StaysWithinTheBudgetWhenABlockCutsARange()
    {
        var block = new[] { "10.0.0.128/25" };
        var carved = SystemRoutes.Carve(Scattered(10, 400), [], block, 100);

        Assert.NotEmpty(carved);
        Assert.True(RouteCount(carved, [], block) <= 100);
    }

    [Fact]
    public void Carve_TakesNothingWhenTheBudgetHoldsNoRange()
    {
        Assert.Empty(SystemRoutes.Carve(["10.0.0.0/8"], [], [], 4));
        Assert.Empty(SystemRoutes.Carve(["10.0.0.0/8"], [], [], 0));
        Assert.Empty(SystemRoutes.Carve([], [], [], 1000));
    }
}
