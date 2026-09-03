using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The ranges a configuration accepts traffic from once access from the tunnel is on: the server alone by
/// default, the whole tunnel network when asked for.
/// </summary>
public sealed class TunnelInboundTests
{
    [Fact]
    public void HostAddress_YieldsTheServerAlone()
    {
        Assert.Equal(["10.8.2.1/32"], TunnelInbound.Ranges(["10.8.2.12/32"], wholeNetwork: false));
    }

    [Fact]
    public void HostAddress_WidensToTheTunnelNetwork()
    {
        Assert.Equal(["10.8.2.0/24"], TunnelInbound.Ranges(["10.8.2.12/32"], wholeNetwork: true));
    }

    [Fact]
    public void DeclaredPrefix_IsKept()
    {
        Assert.Equal(["10.9.0.0/16"], TunnelInbound.Ranges(["10.9.4.7/16"], wholeNetwork: true));
        Assert.Equal(["10.9.0.1/32"], TunnelInbound.Ranges(["10.9.4.7/16"], wholeNetwork: false));
    }

    [Fact]
    public void BareAddress_ReadsAsAHost()
    {
        Assert.Equal(["192.168.9.1/32"], TunnelInbound.Ranges(["192.168.9.30"], wholeNetwork: false));
    }

    [Fact]
    public void Ipv6Address_TakesItsOwnNetwork()
    {
        Assert.Equal(["fd42:6d79:7671::/120"], TunnelInbound.Ranges(["fd42:6d79:7671::9/128"], wholeNetwork: true));
        Assert.Equal(["fd42:6d79:7671::1/128"], TunnelInbound.Ranges(["fd42:6d79:7671::9/128"], wholeNetwork: false));
    }

    [Fact]
    public void Ipv6AddressOfADeepBlock_TakesTheServerOfThatBlock()
    {
        Assert.Equal(["fdcc:ad94:bacf:61a5::cafe:0/120"], TunnelInbound.Ranges(["fdcc:ad94:bacf:61a5::cafe:e/128"], wholeNetwork: true));
        Assert.Equal(["fdcc:ad94:bacf:61a5::cafe:1/128"], TunnelInbound.Ranges(["fdcc:ad94:bacf:61a5::cafe:e/128"], wholeNetwork: false));
    }

    [Fact]
    public void TwoAddressesOfOneNetwork_YieldOneRange()
    {
        Assert.Equal(["10.8.2.1/32"], TunnelInbound.Ranges(["10.8.2.12/32", "10.8.2.13/32"], wholeNetwork: false));
    }

    [Fact]
    public void Garbage_IsSkipped()
    {
        Assert.Empty(TunnelInbound.Ranges(["", "not-an-address"], wholeNetwork: true));
    }
}
