using System.Net;
using AmneziaGeo.Decl;
using AmneziaGeo.Routing;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A name several buckets name gets one path: Block before Direct, Direct before the tunnel, with or without the
/// own network's resolver, and a live edit moves a tracked name the same way.
/// </summary>
public sealed class DnsProxyRulesTests
{
    private static readonly GeoDomain GitHub = new(GeoDomainKind.Domain, "github.com");
    private static readonly GeoDomain YouTube = new(GeoDomainKind.Domain, "youtube.com");
    private static readonly GeoDomain GitHubAds = new(GeoDomainKind.Domain, "ads.github.com");
    private static readonly IPAddress Resolver = IPAddress.Parse("1.1.1.1");

    private static DnsProxy Proxy(IReadOnlyList<GeoDomain> direct, IReadOnlyList<GeoDomain>? block = null, IPAddress? lan = null)
    {
        var pool = lan is null ? Array.Empty<IPAddress>() : new[] { lan };
        return new DnsProxy([GitHub, YouTube], block ?? [], Resolver, Resolver, lan, pool, lan is not null, [], direct, null, NullLogger<DnsProxy>.Instance, stripV6: false, listen: false);
    }

    [Fact]
    public void ADirectName_StaysOffTheTunnel_WithoutTheOwnNetworksResolver()
    {
        var proxy = Proxy([GitHub]);

        Assert.Equal(DnsProxy.NamePath.Direct, proxy.PathOf("api.github.com"));
        Assert.Equal(DnsProxy.NamePath.Open, proxy.PathOf("www.youtube.com"));
    }

    [Fact]
    public void ADirectName_AsksTheOwnNetworksResolver_WhenItIsKnown()
    {
        var proxy = Proxy([GitHub], lan: IPAddress.Parse("192.168.1.1"));

        Assert.Equal(DnsProxy.NamePath.Lan, proxy.PathOf("api.github.com"));
    }

    [Fact]
    public void ANameInSeveralBuckets_IsBlockedFirst_ThenKeptDirect_ThenTunneled()
    {
        var proxy = Proxy([GitHub], [GitHubAds]);

        Assert.Equal(RouteVerdict.Block, proxy.NameVerdict("x.ads.github.com"));
        Assert.Equal(RouteVerdict.Direct, proxy.NameVerdict("api.github.com"));
        Assert.Equal(RouteVerdict.Proxy, proxy.NameVerdict("www.youtube.com"));
    }

    [Fact]
    public void AnEdit_TakesOutOfTheTunnel_ATrackedNameADirectOrBlockRuleNowClaims()
    {
        var proxy = Proxy([]);

        proxy.UpdateBuckets([GitHubAds], [GitHub]);

        var expected = new (string Host, RouteVerdict Verdict)[]
        {
            ("api.github.com", RouteVerdict.Direct),
            ("ads.github.com", RouteVerdict.Block),
            ("gone.example", RouteVerdict.None),
        };
        Assert.Equal(expected, proxy.Departed(["api.github.com", "ads.github.com", "www.youtube.com", "gone.example"]));
    }
}
