using AmneziaGeo.Dal;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The journal rows an agent keeps in memory for the support archive: what goes in, what falls out, and how the
/// rows of two processes read as one.
/// </summary>
public sealed class RecentLogTests
{
    [Fact]
    public void ARowBelowInfo_IsNotKept()
    {
        var recent = new RecentLog();
        recent.Add(1, 2, "agent", "a debug row");
        recent.Add(2, 3, "agent", "an info row");
        recent.Add(3, 5, "agent", "an error row");

        Assert.Equal(["INF", "ERR"], recent.Snapshot().Select(row => row.Level));
    }

    [Fact]
    public void TheOldestRows_FallOutPastTheCapacity()
    {
        var recent = new RecentLog(3);
        for (var i = 1; i <= 5; i++)
        {
            recent.Add(i, 3, "agent", $"row {i}");
        }

        Assert.Equal(["row 3", "row 4", "row 5"], recent.Snapshot().Select(row => row.Message));
    }

    [Fact]
    public void ThePayload_ReadsBackWithItsTabsAndLineBreaks()
    {
        var recent = new RecentLog();
        recent.Add(1700000000123, 4, "tunnel", "first line\nsecond\tcolumn \\ back");

        var row = Assert.Single(RecentLog.Parse(RecentLog.ToPayload(recent.Snapshot())));

        Assert.Equal(1700000000123, row.UnixMs);
        Assert.Equal("WRN", row.Level);
        Assert.Equal("tunnel", row.Source);
        Assert.Equal("first line\nsecond\tcolumn \\ back", row.Message);
    }

    [Fact]
    public void ALineThatIsNotARow_IsSkipped()
    {
        Assert.Empty(RecentLog.Parse("-"));
        Assert.Empty(RecentLog.Parse(null));
        Assert.Single(RecentLog.Parse("oops\n1700000000123\tINF\tagent\tconnected\n"));
    }

    [Fact]
    public void TheRowsOfTwoProcesses_ReadByTime()
    {
        var agent = new[] { Row(1000, "ConfigRunner", "starting the tunnel process"), Row(4000, "ConfigRunner", "connected through srv") };
        var tunnel = new[] { Row(2000, "TunnelRunner", "routing rules ready"), Row(3000, "TunnelRunner", "packet size set to 1420") };

        var merged = RecentLog.Merge(agent, tunnel);

        Assert.Equal([1000L, 2000L, 3000L, 4000L], merged.Select(row => row.UnixMs));
    }

    [Fact]
    public void ARowBothProcessesTold_IsKeptOnce()
    {
        var head = new[] { Row(1000, "agent", "connect requested"), Row(5200, "tunnel", "the session has been raised again") };
        var service = new[] { Row(5000, "tunnel", "the session has been raised again"), Row(90000, "tunnel", "the session has been raised again") };

        var merged = RecentLog.Merge(head, service);

        Assert.Equal([1000L, 5000L, 90000L], merged.Select(row => row.UnixMs));
    }

    private static LogRow Row(long unixMs, string source, string message)
    {
        return new LogRow(0, unixMs, "WRN", source, message);
    }
}
