using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// An app rule moves a process into a cgroup of the agent; when the tunnel goes, the process goes back to the cgroup
/// it lived in, and a child born inside goes where its forebear came from.
/// </summary>
public sealed class CgroupHomesTests
{
    private const string Session = "/user.slice/user-1000.slice/session-2.scope";

    [Fact]
    public void Of_ReadsTheUnifiedCgroup()
    {
        Assert.Equal(Session, CgroupHomes.Of($"0::{Session}\n"));
        Assert.Equal("/", CgroupHomes.Of("0::/\n"));
    }

    [Fact]
    public void Of_FindsTheUnifiedLineAmongTheOldHierarchies()
    {
        var text = $"12:pids:/user.slice/user-1000.slice\n1:name=systemd:{Session}\n0::{Session}\n";

        Assert.Equal(Session, CgroupHomes.Of(text));
    }

    [Fact]
    public void Of_GivesNothingWithoutAUnifiedLine()
    {
        Assert.Null(CgroupHomes.Of("12:pids:/user.slice\n"));
        Assert.Null(CgroupHomes.Of(string.Empty));
    }

    [Fact]
    public void Parent_ReadsTheParentPastANameWithSpacesAndBrackets()
    {
        Assert.Equal(812, CgroupHomes.Parent("4242 (Web Content (x)) S 812 4242 812 0 -1 4194560"));
        Assert.Null(CgroupHomes.Parent("4242 (cut"));
    }

    [Fact]
    public void Back_TakesTheCgroupOfTheProcessItself()
    {
        var homes = new Dictionary<int, string> { [100] = "/sys/fs/cgroup" + Session };

        Assert.Equal("/sys/fs/cgroup" + Session, CgroupHomes.Back(100, homes, _ => 1));
    }

    [Fact]
    public void Back_TakesTheCgroupOfTheNearestForebearForAChildBornInTheGroup()
    {
        var homes = new Dictionary<int, string> { [100] = "/sys/fs/cgroup" + Session, [150] = "/sys/fs/cgroup/other.scope" };
        var parents = new Dictionary<int, int> { [300] = 200, [200] = 100, [100] = 1 };

        var back = CgroupHomes.Back(300, homes, pid => parents.TryGetValue(pid, out var up) ? up : null);

        Assert.Equal("/sys/fs/cgroup" + Session, back);
    }

    [Fact]
    public void Back_GivesNothingWhenNoForebearWasTaken()
    {
        var homes = new Dictionary<int, string> { [100] = "/sys/fs/cgroup" + Session };
        var parents = new Dictionary<int, int> { [300] = 200, [200] = 1 };

        Assert.Null(CgroupHomes.Back(300, homes, pid => parents.TryGetValue(pid, out var up) ? up : null));
        Assert.Null(CgroupHomes.Back(400, homes, _ => null));
    }

    [Fact]
    public void Back_StopsOnParentsThatGoRound()
    {
        var parents = new Dictionary<int, int> { [5] = 6, [6] = 5 };

        Assert.Null(CgroupHomes.Back(5, new Dictionary<int, string>(), pid => parents.TryGetValue(pid, out var up) ? up : null));
    }
}
