using System.Net;
using AmneziaGeo.Decl;
using AmneziaGeo.Routing;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// An answer read from the DNS client's events takes the name rules a query through the proxy takes.
/// </summary>
public sealed class DnsAnswerLearnerTests
{
    private static readonly GeoDomain YouTube = new(GeoDomainKind.Domain, "youtube.com");
    private static readonly GeoDomain GitHub = new(GeoDomainKind.Domain, "github.com");
    private static readonly GeoDomain Corp = new(GeoDomainKind.Domain, "corp.example");
    private static readonly GeoDomain Ads = new(GeoDomainKind.Domain, "ads.example");
    private static readonly IPAddress Resolver = IPAddress.Parse("10.8.1.1");
    private static readonly IReadOnlyList<IPAddress> Answer = [IPAddress.Parse("142.251.154.4")];

    private static DnsProxy Proxy()
    {
        var lan = IPAddress.Parse("192.168.1.1");
        return new DnsProxy([YouTube, GitHub, Corp], [Ads], Resolver, Resolver, lan, [lan], true, ["corp.example"], [GitHub], null, NullLogger<DnsProxy>.Instance, stripV6: false, listen: false);
    }

    [Fact]
    public void Results_TakeIPv4AndMappedAddresses_AndSkipAliasesAndIPv6()
    {
        var addresses = DnsAnswerLearner.ParseResults("type: 5 rr1.sn-n8v7kn7r.googlevideo.com;::ffff:173.194.177.83;142.251.154.4;2a00:1450:4010::65;142.251.154.4;");

        Assert.Equal(new[] { IPAddress.Parse("173.194.177.83"), IPAddress.Parse("142.251.154.4") }, addresses);
    }

    [Fact]
    public void Results_WithoutAddresses_AreEmpty()
    {
        Assert.Empty(DnsAnswerLearner.ParseResults(string.Empty));
        Assert.Empty(DnsAnswerLearner.ParseResults("type: 6 a.gtld-servers.net;"));
        Assert.Empty(DnsAnswerLearner.ParseResults("0.0.0.0;127.0.0.1;"));
    }

    [Fact]
    public void ANameOfTheTunnelList_GoesToTheTunnel_AndTheOthersKeepTheirRules()
    {
        var proxy = Proxy();

        Assert.Equal(RouteVerdict.Proxy, proxy.Learn("www.youtube.com.", Answer, _ => false));
        Assert.Equal(RouteVerdict.Direct, proxy.Learn("api.github.com", Answer, _ => false));
        Assert.Equal(RouteVerdict.Block, proxy.Learn("x.ads.example", Answer, _ => false));
        Assert.Equal(RouteVerdict.None, proxy.Learn("vpn.corp.example", Answer, _ => false));
        Assert.Equal(RouteVerdict.None, proxy.Learn("example.org", Answer, _ => false));
    }
}
