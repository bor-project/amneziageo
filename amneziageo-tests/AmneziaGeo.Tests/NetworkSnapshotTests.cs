using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The verdict a failed request gets from the tunnel and the networks under it: one reading, one place to look.
/// </summary>
public sealed class NetworkSnapshotTests
{
    [Fact]
    public void ADeviceWithNoNetwork_IsTheVerdictWhateverTheTunnel()
    {
        Assert.Equal("the device has no network", Snapshot(under: NetworkSnapshot.NoNetwork).Verdict());
    }

    [Fact]
    public void ATunnelThatIsNotUp_IsNamedByItsStage()
    {
        Assert.Equal("the tunnel is connecting", Snapshot(tunnel: ConnectionStatus.Connecting).Verdict());
    }

    [Fact]
    public void AnExpiredSession_CarriesNothing()
    {
        Assert.Equal("the tunnel carries nothing", Snapshot(handshakeAge: 240).Verdict());
    }

    [Fact]
    public void LostEchoes_CarryNothing()
    {
        Assert.Equal("the tunnel carries nothing", Snapshot(loss: 100).Verdict());
    }

    [Fact]
    public void ASessionReEstablishedOverAndOver_CarriesNothing()
    {
        Assert.Equal("the tunnel carries nothing", Snapshot(churning: true).Verdict());
    }

    [Fact]
    public void AnUnvalidatedTunnelUnderStrictPrivateDns_NamesTheServer()
    {
        var snapshot = Snapshot(tunnelValidated: false, privateDnsHost: "dns.example.net");

        Assert.Equal("the private DNS server dns.example.net is not reached through the tunnel", snapshot.Verdict());
    }

    [Fact]
    public void AnUnvalidatedTunnel_ReadsAsNoInternetThroughIt()
    {
        Assert.Equal("the system sees no internet through the tunnel", Snapshot(tunnelValidated: false).Verdict());
    }

    [Fact]
    public void ALiveTunnel_PutsTheFailureBeyondIt()
    {
        Assert.Equal("the tunnel carries, the failure lies beyond it", Snapshot().Verdict());
    }

    [Fact]
    public void TheDescription_ListsWhatTheVerdictWasReadFrom()
    {
        Assert.Equal(
            "the tunnel carries, the failure lies beyond it (tunnel connected, handshake 12 s ago, receives 3 kbit/s, "
                + "echoes lost 0%, tunnel validated, network mobile, private DNS automatic, in use)",
            Snapshot().Describe());
    }

    [Fact]
    public void AStoppedTunnel_LeavesTheSessionOutOfTheDescription()
    {
        Assert.Equal(
            "the tunnel is disconnected (tunnel disconnected, network Wi-Fi, not validated, private DNS strict dns.example.net)",
            Snapshot(tunnel: ConnectionStatus.Disconnected, under: "Wi-Fi", underValidated: false, privateDnsHost: "dns.example.net").Describe());
    }

    private static NetworkSnapshot Snapshot(
        string tunnel = ConnectionStatus.Connected,
        int handshakeAge = 12,
        int loss = 0,
        bool churning = false,
        string under = "mobile",
        bool? underValidated = true,
        bool? tunnelValidated = true,
        string? privateDnsHost = null)
    {
        return new NetworkSnapshot(tunnel, handshakeAge, 3_000, loss, churning, under, underValidated, tunnelValidated,
            privateDnsHost, true);
    }
}
