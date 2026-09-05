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
}
