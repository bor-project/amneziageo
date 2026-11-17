using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The hello reaches the services of a config over the connections the head opens for it, and over the usual ones
/// where the head hands no way.
/// </summary>
public sealed class ServerHelloTests
{
    [Fact]
    public async Task TheQuestion_TravelsTheWayItIsHanded()
    {
        using var panel = new TlsPanel(status: 404);
        var dialed = 0;

        var reply = await ServerHello.AskAsync(
            new ServiceTarget("localhost", panel.Port, Key(), Key()) { TlsPin = panel.Pin },
            async (context, ct) =>
            {
                Interlocked.Increment(ref dialed);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, context.DnsEndPoint.Port), ct);

                return new NetworkStream(socket, ownsSocket: true);
            },
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(reply.Heard);
        Assert.Null(reply.Offer);
        Assert.Equal(1, dialed);
        Assert.Equal(1, panel.Served);
    }

    [Fact]
    public async Task TheQuestionWithNoWayHanded_TravelsTheUsualOne()
    {
        using var panel = new TlsPanel(status: 404);

        var reply = await ServerHello.AskAsync(new ServiceTarget("localhost", panel.Port, Key(), Key()) { TlsPin = panel.Pin }, CancellationToken.None).ConfigureAwait(false);

        Assert.True(reply.Heard);
        Assert.Equal(1, panel.Served);
    }

    [Fact]
    public async Task APortThatRefusesTheConnection_IsHeardAsAServerOfAnotherKind()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var reply = await ServerHello.AskAsync(new ServiceTarget("127.0.0.1", port, Key(), Key()), CancellationToken.None);

        Assert.True(reply.Heard);
        Assert.Null(reply.Offer);
    }

    [Fact]
    public async Task AnErrorOfTheServer_TellsNothing()
    {
        using var panel = new TlsPanel(status: 500);

        var reply = await ServerHello.AskAsync(new ServiceTarget("localhost", panel.Port, Key(), Key()) { TlsPin = panel.Pin }, CancellationToken.None).ConfigureAwait(false);

        Assert.False(reply.Heard);
        Assert.Null(reply.Offer);
        Assert.Equal(1, panel.Served);
    }

    [Theory]
    [InlineData(401, "hello-auth")]
    [InlineData(403, "hello-auth")]
    [InlineData(404, "hello-bad-answer")]
    [InlineData(500, "hello-unavailable")]
    public async Task HttpFailuresNameTheirReason(int status, string fault)
    {
        using var panel = new TlsPanel(status: status);

        var reply = await ServerHello.AskAsync(new ServiceTarget("localhost", panel.Port, Key(), Key()) { TlsPin = panel.Pin }, CancellationToken.None).ConfigureAwait(false);

        Assert.Null(reply.Offer);
        Assert.Equal(fault, reply.Fault);
    }

    [Fact]
    public async Task ARejectedCertificatePinNamesTlsAndSendsNoHello()
    {
        using var panel = new TlsPanel(status: 200);
        var point = new ServiceTarget("localhost", panel.Port, Key(), Key()) { TlsPin = new string('0', 64) };

        var reply = await ServerHello.AskAsync(point, CancellationToken.None).ConfigureAwait(false);

        Assert.False(reply.Heard);
        Assert.Equal("hello-tls", reply.Fault);
        Assert.Equal(0, panel.Served);
    }

    [Fact]
    public async Task CallerCancellationIsNotAnAnsweredHello()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ServerHello.AskAsync(
            new ServiceTarget("localhost", 443, Key(), Key()), cancellation.Token)).ConfigureAwait(false);
    }

    private static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
