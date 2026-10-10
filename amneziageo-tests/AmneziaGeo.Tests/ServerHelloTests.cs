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
            new ServiceTarget("localhost", panel.Port, Key(), Key()),
            async (context, ct) =>
            {
                Interlocked.Increment(ref dialed);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, context.DnsEndPoint.Port), ct);

                return new NetworkStream(socket, ownsSocket: true);
            },
            CancellationToken.None);

        Assert.True(reply.Heard);
        Assert.Null(reply.Offer);
        Assert.Equal(1, dialed);
        Assert.Equal(1, panel.Served);
    }

    [Fact]
    public async Task TheQuestionWithNoWayHanded_TravelsTheUsualOne()
    {
        using var panel = new TlsPanel(status: 404);

        var reply = await ServerHello.AskAsync(new ServiceTarget("localhost", panel.Port, Key(), Key()), CancellationToken.None);

        Assert.True(reply.Heard);
        Assert.Equal(1, panel.Served);
    }

    private static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
