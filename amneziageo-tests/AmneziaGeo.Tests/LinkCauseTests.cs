using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A tunnel that stopped carrying is told by the place it is broken: the device has no way out, the internet answers
/// and the server does not, the server answers and the tunnel carries nothing, or the place cannot be told.
/// </summary>
public sealed class LinkCauseTests
{
    private static readonly IPAddress Server = IPAddress.Parse("198.51.100.7");
    private static readonly IPEndPoint Base = new(IPAddress.Parse("203.0.113.9"), 443);

    [Fact]
    public void ADeviceWithoutANetwork_IsToldSo()
    {
        var report = LinkCause.Decide(Options(network: false), null, null, null);

        Assert.Equal(LinkCause.NoNetwork, report.Cause);
    }

    [Theory]
    [InlineData(30, "its last handshake was 30 s ago")]
    [InlineData(-1, "gets no answer to its handshake")]
    [InlineData(600, "gets no answer to its handshake")]
    public void AServerThatAnswers_LeavesTheTunnel(int handshake, string said)
    {
        var report = LinkCause.Decide(Options(handshake: handshake), true, true, null);

        Assert.Equal(LinkCause.TunnelStuck, report.Cause);
        Assert.Contains(said, report.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASilentServerWhileABaseAnswers_IsTheServer()
    {
        var report = LinkCause.Decide(Options(), true, false, Base);

        Assert.Equal(LinkCause.ServerSilent, report.Cause);
        Assert.Contains(Base.ToString(), report.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "no further than the gateway")]
    [InlineData(false, "no working network")]
    [InlineData(null, "no way out to the internet")]
    public void NothingAnsweringPastTheGateway_IsTheNetwork(bool? gateway, string said)
    {
        var report = LinkCause.Decide(Options(), gateway, false, null);

        Assert.Equal(LinkCause.NoNetwork, report.Cause);
        Assert.Contains(said, report.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASilentServerWithNoBaseKnown_CannotBeTold()
    {
        var alone = LinkCause.Decide(Options(bases: []), true, false, null);
        var itself = LinkCause.Decide(Options(bases: [new IPEndPoint(Server, 443)]), true, false, null);

        Assert.Equal(LinkCause.Unknown, alone.Cause);
        Assert.Equal(LinkCause.Unknown, itself.Cause);
    }

    [Fact]
    public void AServerOfNoKnownAddress_CannotBeTold()
    {
        var report = LinkCause.Decide(Options() with { Server = null }, true, null, Base);

        Assert.Equal(LinkCause.Unknown, report.Cause);
    }

    [Fact]
    public async Task AServerWhosePortRefuses_Answers()
    {
        var closed = ClosedPort();

        var report = await LinkCause.FindAsync(
            new LinkCauseOptions(null, null, IPAddress.Loopback, closed, [], 30, TimeoutMs: 500),
            CancellationToken.None);

        Assert.Equal(LinkCause.TunnelStuck, report.Cause);
    }

    [Fact]
    public async Task ABaseThatTakesTheConnection_TellsTheInternetFromASilentServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var open = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);

            var report = await LinkCause.FindAsync(
                new LinkCauseOptions(null, null, IPAddress.Parse("192.0.2.1"), 9, [open], 30, TimeoutMs: 300),
                CancellationToken.None);

            Assert.Equal(LinkCause.ServerSilent, report.Cause);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TheAddressesOfTheBases_AreTheHostsTheyName()
    {
        var found = await LinkCause.ResolveAsync(
            ["https://203.0.113.9/a.dat", "https://203.0.113.9/b.dat", "http://198.51.100.20:8080/c", "ftp://192.0.2.5/d", "not a url"],
            CancellationToken.None);

        Assert.Equal([new IPEndPoint(IPAddress.Parse("203.0.113.9"), 443), new IPEndPoint(IPAddress.Parse("198.51.100.20"), 8080)], found);
    }

    private static LinkCauseOptions Options(bool? network = true, int handshake = 30, IReadOnlyList<IPEndPoint>? bases = null) =>
        new(network, "192.0.2.254", Server, 51820, bases ?? [Base], handshake);

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}
