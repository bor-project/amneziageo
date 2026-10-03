using System.Net;
using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The machine itself, no address at all, a link of its own, a group and the reserved block never ride a tunnel;
/// a public address and a private one behind the server may.
/// </summary>
public sealed class SpecialAddressesTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.10")]
    [InlineData("169.254.1.1")]
    [InlineData("224.0.0.251")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::fb")]
    [InlineData("::ffff:127.0.0.1")]
    public void AnAddressOfTheMachineALinkOrAGroup_IsSpecial(string address)
    {
        Assert.True(SpecialAddresses.Holds(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("10.8.28.41")]
    [InlineData("100.64.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.39")]
    [InlineData("169.253.255.255")]
    [InlineData("169.255.0.0")]
    [InlineData("223.255.255.255")]
    [InlineData("2606:4700::1111")]
    [InlineData("fc00::1")]
    [InlineData("::ffff:1.1.1.1")]
    public void APublicOrAPrivateAddress_IsNotSpecial(string address)
    {
        Assert.False(SpecialAddresses.Holds(IPAddress.Parse(address)));
    }
}
