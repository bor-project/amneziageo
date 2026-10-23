using System.Net.NetworkInformation;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// An adapter of another VPN is told from the client's own and from the rest, and the phonebook of the connections
/// gives the servers its VPN entries dial.
/// </summary>
public sealed class OtherVpnsTests
{
    [Theory]
    [InlineData(NetworkInterfaceType.Ppp, "VPN work", "WAN Miniport (SSTP)", "PPP")]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet 3", "PANGP Virtual Ethernet Adapter Secure", "PANGP")]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet 4", "TAP-Windows Adapter V9", "TAP-Windows")]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet 5", "Fortinet SSL VPN Virtual Ethernet Adapter", "Fortinet")]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet", "Intel(R) Ethernet Connection", null)]
    [InlineData(NetworkInterfaceType.Ethernet, "AmneziaGeo$work", "Wintun Userspace Tunnel", null)]
    [InlineData(NetworkInterfaceType.Ethernet, "wg0", "WireGuard Tunnel", null)]
    [InlineData(NetworkInterfaceType.Tunnel, "Teredo", "Teredo Tunneling Pseudo-Interface", null)]
    [InlineData(NetworkInterfaceType.Loopback, "Loopback", "Software Loopback Interface 1", null)]
    public void AnAdapter_IsAnotherVpn_ByItsKindOrItsClient(NetworkInterfaceType type, string name, string description, string? expected)
    {
        Assert.Equal(expected, OtherVpns.KindOf(type, name, description));
    }

    [Fact]
    public void ThePhonebook_GivesTheServersOfItsVpnEntries()
    {
        const string text = """
            [Office]
            Encoding=1
            PBVersion=6
            Type=2
            AutoLogon=0
            DEVICE=vpn
            PhoneNumber=vpn.example.com
            AreaCode=
            CountryCode=0

            [Dial-up]
            Type=1
            DEVICE=modem
            PhoneNumber=5551234

            [Branch]
            Type=2
            PhoneNumber=198.51.100.7
            PhoneNumber=vpn2.example.com

            [Office again]
            Type=2
            PhoneNumber=VPN.example.com
            """;

        Assert.Equal(["vpn.example.com", "198.51.100.7"], OtherVpns.ParsePhonebook(text));
    }

    [Fact]
    public void AnEmptyPhonebook_GivesNoServers()
    {
        Assert.Empty(OtherVpns.ParsePhonebook(string.Empty));
        Assert.Empty(OtherVpns.ParsePhonebook("[Entry]\r\nType=2\r\n"));
    }

    [Theory]
    [InlineData("10.20.30.40", "10.0.0.0", 8)]
    [InlineData("127.0.0.1", "127.0.0.0", 8)]
    [InlineData("172.16.5.9", "172.16.0.0", 16)]
    [InlineData("191.255.1.1", "191.255.0.0", 16)]
    [InlineData("192.168.30.17", "192.168.30.0", 24)]
    [InlineData("203.0.113.9", "203.0.113.0", 24)]
    public void TheNetworkOfAnAddress_FollowsItsClass(string address, string network, byte prefix)
    {
        var (found, length) = OtherVpnKeeper.ClassOf(System.Net.IPAddress.Parse(address));

        Assert.Equal(System.Net.IPAddress.Parse(network), found);
        Assert.Equal(prefix, length);
    }
}
