using System.Net;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The verdict is what replaces pre-installed routes: it is asked per destination, so its precedence must match
/// the eager path - Block over Direct, tunnel by default.
/// </summary>
public sealed class VerdictResolverTests
{
    [Fact]
    public void AddressInDirectSet_IsDirect()
    {
        var resolver = VerdictResolver.Build(["77.88.32.0/19"], []);

        Assert.Equal(RouteVerdict.Direct, resolver.Classify(IPAddress.Parse("77.88.55.242")));
    }

    [Fact]
    public void AddressOutsideEverySet_GoesToTheTunnel()
    {
        var resolver = VerdictResolver.Build(["77.88.32.0/19"], []);

        Assert.Equal(RouteVerdict.Proxy, resolver.Classify(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void BlockWinsOverDirect_SoABlockedAddressNeverEarnsABypass()
    {
        var resolver = VerdictResolver.Build(["10.0.0.0/8"], ["10.1.2.0/24"]);

        Assert.Equal(RouteVerdict.Block, resolver.Classify(IPAddress.Parse("10.1.2.3")));
        Assert.Equal(RouteVerdict.Direct, resolver.Classify(IPAddress.Parse("10.1.3.3")));
    }

    [Fact]
    public void RepeatQuery_AnswersFromTheMemo()
    {
        var resolver = VerdictResolver.Build(["77.88.32.0/19"], []);
        var address = IPAddress.Parse("77.88.55.242");

        var first = resolver.Classify(address);
        var second = resolver.Classify(address);

        Assert.Equal(first, second);
        Assert.Equal(RouteVerdict.Direct, second);
    }

    [Fact]
    public void InvalidatedMemo_StillClassifiesTheSameWay()
    {
        var resolver = VerdictResolver.Build(["77.88.32.0/19"], []);
        var address = IPAddress.Parse("77.88.55.242");
        _ = resolver.Classify(address);

        resolver.Invalidate();

        Assert.Equal(RouteVerdict.Direct, resolver.Classify(address));
    }

    [Fact]
    public void Ipv6_IsLeftToTheTunnel()
    {
        var resolver = VerdictResolver.Build(["10.0.0.0/8"], ["10.1.2.0/24"]);

        Assert.Equal(RouteVerdict.Proxy, resolver.Classify(IPAddress.Parse("2a02:6b8::2:242")));
    }

    [Fact]
    public void EmptyRules_ReportNoRules()
    {
        var resolver = VerdictResolver.Build([], []);

        Assert.False(resolver.HasRules);
        Assert.Equal(RouteVerdict.Proxy, resolver.Classify(IPAddress.Parse("1.2.3.4")));
    }
}
