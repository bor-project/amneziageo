using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// What the carrier process of a tunnel is started with. Its lines go to the journal of the client, so it is told
/// to write them without colour codes; the rest names the front, the proof and the port the tunnel is handed to.
/// </summary>
public sealed class CarrierArgumentsTests
{
    [Fact]
    public void TheCarrierIsToldToWriteWithoutColours()
    {
        var arguments = WsTunnelTransport.Arguments(true, @"C:\data\ws-headers-home.txt", "v1", string.Empty, 52027, 51820, "vpn.example", 443);

        Assert.StartsWith("--no-color true client ", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void AFrontTheServerOffered_IsShownTheTokenAndItsCertificateIsTakenAsItStands()
    {
        var arguments = WsTunnelTransport.Arguments(true, @"C:\data\ws-headers-home.txt", "v1", string.Empty, 52027, 51820, "vpn.example", 443);

        Assert.Contains(@" --http-headers-file ""C:\data\ws-headers-home.txt""", arguments, StringComparison.Ordinal);
        Assert.Contains(@" -P ""v1""", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--tls-verify-certificate", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--http-upgrade-credentials", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void AFrontOfTheSettings_HasItsCertificateVerified_AndIsGivenTheAccount()
    {
        var arguments = WsTunnelTransport.Arguments(false, string.Empty, string.Empty, "user:pass", 52027, 51820, "vpn.example", 8443);

        Assert.Contains(" --tls-verify-certificate", arguments, StringComparison.Ordinal);
        Assert.Contains(@" --http-upgrade-credentials ""user:pass""", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--http-headers-file", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain(" -P ", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTunnelIsTakenFromTheLoopback_AndHandedToThePortOfTheInterface()
    {
        var arguments = WsTunnelTransport.Arguments(false, string.Empty, string.Empty, string.Empty, 52027, 51820, "vpn.example", 8443);

        Assert.EndsWith(@" -L ""udp://52027:127.0.0.1:51820?timeout_sec=0"" ""wss://vpn.example:8443""", arguments, StringComparison.Ordinal);
    }
}
