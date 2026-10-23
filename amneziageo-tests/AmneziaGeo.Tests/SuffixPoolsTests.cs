using System.Net;
using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A name under the suffix of an adapter or a VPN is answered by the resolvers that came with the suffix, the longest
/// suffix first.
/// </summary>
public sealed class SuffixPoolsTests
{
    private static readonly IPAddress Office = IPAddress.Parse("10.20.0.53");
    private static readonly IPAddress Branch = IPAddress.Parse("10.30.0.53");
    private static readonly IPAddress Lan = IPAddress.Parse("192.168.1.1");

    [Fact]
    public void TheLongestSuffix_WinsAndCaseAndDotsDoNotCount()
    {
        var pools = SuffixPools.Normalize([("Corp.Example.", [Office]), ("branch.corp.example", [Branch]), (string.Empty, [Lan]), ("empty.example", [])]);

        Assert.Equal(["branch.corp.example", "corp.example"], pools.Select(pool => pool.Suffix));
        Assert.Equal([Branch], SuffixPools.PoolOf(pools, "nas.branch.corp.example."));
        Assert.Equal([Office], SuffixPools.PoolOf(pools, "NAS.CORP.EXAMPLE"));
        Assert.Equal([Office], SuffixPools.PoolOf(pools, "corp.example"));
        Assert.Null(SuffixPools.PoolOf(pools, "notcorp.example"));
        Assert.Null(SuffixPools.PoolOf(pools, "api.github.com"));
    }

    [Fact]
    public void TheSameSets_AreToldFromAChangedOne()
    {
        var first = SuffixPools.Normalize([("corp.example", [Office])]);

        Assert.True(SuffixPools.Same(first, SuffixPools.Normalize([("CORP.example", [Office])])));
        Assert.False(SuffixPools.Same(first, SuffixPools.Normalize([("corp.example", [Branch])])));
        Assert.False(SuffixPools.Same(first, SuffixPools.Normalize([])));
    }
}
