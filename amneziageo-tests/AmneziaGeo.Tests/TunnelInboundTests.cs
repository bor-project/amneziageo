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

    [Fact]
    public void Hosts_DropThePrefixLength()
    {
        Assert.Equal(["10.9.9.11", "fde1:c450:1259:9::11"], TunnelInbound.Hosts(["10.9.9.11/24", "fde1:c450:1259:9::11/64"]));
    }

    [Fact]
    public void Hosts_KeepABareAddressAndSkipGarbage()
    {
        Assert.Equal(["10.9.9.11"], TunnelInbound.Hosts(["10.9.9.11", "", "not-an-address"]));
    }

    [Fact]
    public void Hosts_YieldOneEntryPerAddress()
    {
        Assert.Equal(["10.9.9.11"], TunnelInbound.Hosts(["10.9.9.11/24", "10.9.9.11/32"]));
    }
    [Fact]
    public void PrefixOfTheCoveringAllowedIp_Wins()
    {
        Assert.Equal(["10.9.0.0/16"], TunnelInbound.Ranges(["10.9.4.7/32"], ["10.9.0.0/16", "192.168.1.0/24"], wholeNetwork: true));
        Assert.Equal(["10.9.0.1/32"], TunnelInbound.Ranges(["10.9.4.7/32"], ["10.9.0.0/16"], wholeNetwork: false));
    }

    [Fact]
    public void NarrowestCoveringNetwork_Wins()
    {
        Assert.Equal(["10.9.4.0/24"], TunnelInbound.Ranges(["10.9.4.7/32"], ["10.0.0.0/8", "10.9.0.0/16", "10.9.4.0/24"], wholeNetwork: true));
    }

    [Fact]
    public void FullTunnelAndForeignNetworks_LeaveTheAddressAlone()
    {
        Assert.Equal(["10.9.4.0/24"], TunnelInbound.Ranges(["10.9.4.7/32"], ["0.0.0.0/0", "192.168.1.0/24"], wholeNetwork: true));
    }

    [Fact]
    public void Ipv6PrefixComesFromTheCoveringAllowedIp()
    {
        Assert.Equal(["fd42:6d79:7671::/64"], TunnelInbound.Ranges(["fd42:6d79:7671::9/128"], ["fd42:6d79:7671::/64"], wholeNetwork: true));
    }
}
