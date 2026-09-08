using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

public class PrivateNetworksTests
{
    [Fact]
    public void PrivateNetworksOfAConfig_AreCollected()
    {
        var config = "[Peer]\nAllowedIPs = 192.168.1.0/24, 10.0.0.0/8, fde1:c450:1259::/48";
        Assert.Equal(["10.0.0.0/8", "192.168.1.0/24", "fde1:c450:1259::/48"], PrivateNetworks.FromConfigs([config]));
    }

    [Fact]
    public void HostsPublicRangesAndTheWholeInternet_AreLeftOut()
    {
        var config = "[Peer]\nAllowedIPs = 0.0.0.0/0, ::/0, 8.8.8.8/32, 10.0.1.1/32, 172.32.0.0/16";
        Assert.Empty(PrivateNetworks.FromConfigs([config]));
    }

    [Fact]
    public void TheSameNetworkInTwoConfigs_StandsOnce()
    {
        var first = "[Peer]\nAllowedIPs = 192.168.1.0/24";
        var second = "[Peer]\nAllowedIPs = 192.168.1.0/24, 192.168.0.0/24";
        Assert.Equal(["192.168.0.0/24", "192.168.1.0/24"], PrivateNetworks.FromConfigs([first, second]));
    }

    [Fact]
    public void TheNetworksOfOneConfig_KeepTheOrderItNamesThem()
    {
        var config = "[Interface]\nAddress = 10.9.9.13/24\n[Peer]\nAllowedIPs = 10.9.9.0/24, 192.168.1.0/24, 0.0.0.0/0";
        Assert.Equal(["10.9.9.0/24", "192.168.1.0/24"], PrivateNetworks.FromConfig(config));
    }

    [Fact]
    public void TheNetworksOfATunnel_TakeTheAddressNetworkAlongsideTheAllowedOnes()
    {
        var config = "[Interface]\nAddress = 10.9.9.13/24\n[Peer]\nAllowedIPs = 10.9.9.0/24, 192.168.1.0/24";
        Assert.Equal(["10.9.9.0/24", "192.168.1.0/24"], PrivateNetworks.ForTunnel(config, []));
    }

    [Fact]
    public void TheNetworkOfABareAddress_StandsWhileThePeerAllowsEverything()
    {
        var config = "[Interface]\nAddress = 10.9.9.13/32\n[Peer]\nAllowedIPs = 0.0.0.0/0";
        Assert.Equal(["10.9.9.0/24"], PrivateNetworks.ForTunnel(config, []));
    }

    [Fact]
    public void ANetworkTheMachineStandsIn_IsLeftOutOfTheTunnelNetworks()
    {
        var config = "[Interface]\nAddress = 10.9.9.13/24\n[Peer]\nAllowedIPs = 10.9.9.0/24, 192.168.1.0/24";
        Assert.Equal(["10.9.9.0/24"], PrivateNetworks.ForTunnel(config, ["192.168.1.47/24"]));
    }

    [Fact]
    public void ANetworkTheMachineStandsIn_IsFoundAmongItsOwn()
    {
        Assert.True(PrivateNetworks.Overlaps("192.168.1.0/24", ["10.0.110.0/24", "192.168.1.0/24"]));
        Assert.True(PrivateNetworks.Overlaps("192.168.1.0/24", ["192.168.0.0/16"]));
        Assert.True(PrivateNetworks.Overlaps("192.168.0.0/16", ["192.168.1.0/24"]));
    }

    [Fact]
    public void ANetworkBeyondTheMachine_IsNotFoundAmongItsOwn()
    {
        Assert.False(PrivateNetworks.Overlaps("10.9.9.0/24", ["10.0.110.0/24", "192.168.1.0/24"]));
        Assert.False(PrivateNetworks.Overlaps("192.168.2.0/24", ["192.168.1.0/24"]));
        Assert.False(PrivateNetworks.Overlaps("fde1:c450:1259::/48", ["192.168.1.0/24"]));
        Assert.False(PrivateNetworks.Overlaps("not a network", ["192.168.1.0/24"]));
    }
}
