using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// An address a tunnel would take by a route of its own is left alone when it lies inside a network the machine
/// stands in: the resolver of a configuration and the address its server signals from are such addresses.
/// </summary>
public sealed class LocalHostsTests
{
    private static readonly string[] _home = ["192.168.1.0/24", "172.20.16.0/20"];

    [Fact]
    public void AnAddressInsideANetworkOfTheMachine_IsFound()
    {
        Assert.Equal(["192.168.1.1"], PrivateNetworks.InsideLocal(["192.168.1.1", "10.255.0.1", "1.1.1.1"], _home));
    }

    [Fact]
    public void EveryNetworkOfTheMachine_IsLookedAt()
    {
        Assert.Equal(["172.20.31.254", "192.168.1.254"], PrivateNetworks.InsideLocal(["172.20.31.254", "172.20.32.1", "192.168.1.254", "192.168.2.1"], _home));
    }

    [Fact]
    public void AnAddressOfAnotherFamilyOrNoAddressAtAll_IsNotFound()
    {
        Assert.Empty(PrivateNetworks.InsideLocal(["fd00::1", "resolver.example", string.Empty], _home));
    }

    [Fact]
    public void AMachineInNoNetwork_KeepsNothingBack()
    {
        Assert.Empty(PrivateNetworks.InsideLocal(["192.168.1.1"], []));
    }
}
