using System.Net;
using System.Net.Sockets;
using System.Text;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The port a carried tunnel dials: what crosses it, and what a carrier or a tunnel that went away leaves of it.
/// </summary>
public sealed class CarrierPortTests : IDisposable
{
    private const int WaitMs = 3000;
    private const int QuietMs = 300;

    private Socket _carrier = Bound();
    private Socket _tunnel = Bound();

    /// <inheritdoc/>
    public void Dispose()
    {
        _carrier.Dispose();
        _tunnel.Dispose();
    }

    [Fact]
    public void WhatTheTunnelSends_ReachesTheCarrier_AndTheAnswerComesBack()
    {
        using var port = Open();

        Send(_tunnel, port.Port, "out");
        var heard = Take(_carrier);
        _carrier.SendTo(Encoding.ASCII.GetBytes("back"), heard.From);

        Assert.Equal("out", heard.Said);
        Assert.Equal("back", Take(_tunnel).Said);
    }

    [Fact]
    public void ACarrierThatWentAwayAndCameBack_IsCarriedToAgain()
    {
        using var port = Open();
        Send(_tunnel, port.Port, "one");
        Take(_carrier);

        var held = PortOf(_carrier);
        _carrier.Dispose();
        Send(_tunnel, port.Port, "lost");
        Send(_tunnel, port.Port, "lost");
        _carrier = Bound(held);

        var heard = Until(_carrier, "again", () => Send(_tunnel, port.Port, "again"));
        _carrier.SendTo(Encoding.ASCII.GetBytes("back"), heard.From);

        Assert.Equal("again", heard.Said);
        Assert.Equal("back", Take(_tunnel).Said);
    }

    [WindowsFact("a datagram to a port nobody holds resets its sender there")]
    public void ADatagramToAPortNobodyHolds_ResetsItsSender()
    {
        var gone = PortOf(_carrier);
        _carrier.Dispose();

        Send(_tunnel, gone, "lost");

        Assert.True(_tunnel.Poll(WaitMs * 1000, SelectMode.SelectRead));
        var ex = Assert.Throws<SocketException>(() => _tunnel.Receive(new byte[16]));
        Assert.Equal(SocketError.ConnectionReset, ex.SocketErrorCode);
    }

    [WindowsFact("a datagram to a port nobody holds resets its sender there")]
    public void ATunnelThatSendsWhileTheCarrierIsAway_GetsNoResetOnItsSocket()
    {
        using var port = Open();
        Send(_tunnel, port.Port, "one");
        Take(_carrier);

        _carrier.Dispose();
        Send(_tunnel, port.Port, "lost");

        Assert.False(_tunnel.Poll(QuietMs * 1000, SelectMode.SelectRead));
    }

    [Fact]
    public void ATunnelBoundToAnotherPort_IsAnsweredAtTheNewOne()
    {
        using var port = Open();
        Send(_tunnel, port.Port, "one");
        Take(_carrier);

        _tunnel.Dispose();
        _tunnel = Bound();
        Send(_tunnel, port.Port, "two");
        var heard = Take(_carrier);
        _carrier.SendTo(Encoding.ASCII.GetBytes("back"), heard.From);

        Assert.Equal("two", heard.Said);
        Assert.Equal("back", Take(_tunnel).Said);
    }

    [Fact]
    public void AnAnswerToATunnelPortThatClosed_LeavesThePortCarrying()
    {
        using var port = Open();
        Send(_tunnel, port.Port, "one");
        var first = Take(_carrier);

        _tunnel.Dispose();
        _carrier.SendTo(Encoding.ASCII.GetBytes("late"), first.From);
        _carrier.SendTo(Encoding.ASCII.GetBytes("late"), first.From);
        _tunnel = Bound();

        var heard = Until(_carrier, "two", () => Send(_tunnel, port.Port, "two"));
        _carrier.SendTo(Encoding.ASCII.GetBytes("back"), heard.From);

        Assert.Equal("two", heard.Said);
        Assert.Equal("back", Take(_tunnel).Said);
    }

    [WindowsFact("a datagram to a port nobody holds resets its sender there")]
    public void ACarrierThatAnswersATunnelPortThatClosed_GetsNoResetOnItsSocket()
    {
        using var port = Open();
        Send(_tunnel, port.Port, "one");
        var first = Take(_carrier);

        _tunnel.Dispose();
        _carrier.SendTo(Encoding.ASCII.GetBytes("late"), first.From);

        Assert.False(_carrier.Poll(QuietMs * 1000, SelectMode.SelectRead));
    }

    [Fact]
    public void ADatagramFromAPortTheTunnelDoesNotHold_IsDropped_AndTakesNoAnswers()
    {
        var own = PortOf(_tunnel);
        using var port = Open(sender => sender == own);
        using var stranger = Bound();
        Send(_tunnel, port.Port, "one");
        var first = Take(_carrier);

        Send(stranger, port.Port, "junk");
        var crossed = _carrier.Poll(QuietMs * 1000, SelectMode.SelectRead);
        _carrier.SendTo(Encoding.ASCII.GetBytes("back"), first.From);

        Assert.False(crossed);
        Assert.Equal("back", Take(_tunnel).Said);
        Assert.False(stranger.Poll(QuietMs * 1000, SelectMode.SelectRead));
    }

    [Fact]
    public void ACarrierOfferedBesideThePresentOne_GetsTheDatagramsToo()
    {
        using var port = Open();
        using var second = Bound();
        Send(_tunnel, port.Port, "one");
        Take(_carrier);

        port.Offer(PortOf(second));
        Send(_tunnel, port.Port, "two");

        Assert.Equal("two", Take(_carrier).Said);
        Assert.Equal("two", TakeNamed(second, "two").Said);
    }

    [Fact]
    public void AnAnswerOfTheCarrierOffered_ReachesTheTunnel_AndCountsAsTaken()
    {
        using var port = Open();
        using var second = Bound();
        Send(_tunnel, port.Port, "one");
        Take(_carrier);
        port.Offer(PortOf(second));
        Send(_tunnel, port.Port, "two");
        var heard = TakeNamed(second, "two");
        var before = port.Taken;

        second.SendTo(Encoding.ASCII.GetBytes("back"), heard.From);

        Assert.False(before);
        Assert.Equal("back", Take(_tunnel).Said);
        Assert.True(port.Taken);
    }

    [Fact]
    public void AfterTheSwitch_TheNewCarrierAloneGetsTheDatagrams_AndTheOneBeforeIsHeardUntilForgotten()
    {
        using var port = Open();
        using var second = Bound();
        Send(_tunnel, port.Port, "one");
        var first = Take(_carrier);
        port.Offer(PortOf(second));

        port.Switch();
        Send(_tunnel, port.Port, "two");
        var crossed = _carrier.Poll(QuietMs * 1000, SelectMode.SelectRead);
        _carrier.SendTo(Encoding.ASCII.GetBytes("late"), first.From);
        var late = Take(_tunnel).Said;
        port.Forget();
        _carrier.SendTo(Encoding.ASCII.GetBytes("gone"), first.From);

        Assert.Equal("two", TakeNamed(second, "two").Said);
        Assert.False(crossed);
        Assert.Equal("late", late);
        Assert.False(_tunnel.Poll(QuietMs * 1000, SelectMode.SelectRead));
    }

    [Fact]
    public void ACarrierWithdrawn_GetsNoMoreDatagrams()
    {
        using var port = Open();
        using var second = Bound();
        port.Offer(PortOf(second));
        Send(_tunnel, port.Port, "one");
        Take(second);

        port.Withdraw();
        Send(_tunnel, port.Port, "two");

        Assert.Equal("one", Take(_carrier).Said);
        Assert.Equal("two", Take(_carrier).Said);
        Assert.False(second.Poll(QuietMs * 1000, SelectMode.SelectRead));
    }

    [Fact]
    public void TheSilenceOfThePresentCarrier_IsCountedFromItsLastDatagram()
    {
        using var port = Open();
        Send(_tunnel, port.Port, "one");
        var first = Take(_carrier);
        Thread.Sleep(200);
        var silent = port.QuietMs;

        _carrier.SendTo(Encoding.ASCII.GetBytes("back"), first.From);
        Take(_tunnel);

        Assert.True(silent >= 150, $"the silence read {silent} ms");
        Assert.True(port.QuietMs < silent, $"the silence read {port.QuietMs} ms after an answer");
    }

    [WindowsFact("the ports of a process are read from the table Windows keeps")]
    public void APortOfThisProcess_CountsAsTheTunnel()
    {
        using var port = new CarrierPort(PortOf(_carrier), NullLogger.Instance);

        Send(_tunnel, port.Port, "out");

        Assert.Equal("out", Take(_carrier).Said);
    }

    private CarrierPort Open(Func<int, bool>? owned = null)
    {
        return new CarrierPort(PortOf(_carrier), NullLogger.Instance, owned ?? (_ => true));
    }

    private static Socket Bound(int port = 0)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        return socket;
    }

    private static int PortOf(Socket socket)
    {
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static void Send(Socket socket, int port, string text)
    {
        socket.SendTo(Encoding.ASCII.GetBytes(text), new IPEndPoint(IPAddress.Loopback, port));
    }

    // Takes the next datagram of a socket, failing when none comes in time.
    private static (string Said, EndPoint From) Take(Socket socket)
    {
        Assert.True(socket.Poll(WaitMs * 1000, SelectMode.SelectRead), "nothing came in time");
        var buffer = new byte[64];
        var from = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
        var received = socket.ReceiveFrom(buffer, ref from);
        return (Encoding.ASCII.GetString(buffer, 0, received), from);
    }

    // Takes the datagrams of a socket until the one named comes.
    private static (string Said, EndPoint From) TakeNamed(Socket socket, string wanted)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var heard = Take(socket);
            if (heard.Said == wanted)
            {
                return heard;
            }
        }

        Assert.Fail($"'{wanted}' never came");
        return default;
    }

    // Repeats a send until the socket takes the datagram named.
    private static (string Said, EndPoint From) Until(Socket socket, string wanted, Action send)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            send();
            if (socket.Poll(100_000, SelectMode.SelectRead))
            {
                var heard = Take(socket);
                if (heard.Said == wanted)
                {
                    return heard;
                }
            }
        }

        Assert.Fail($"'{wanted}' never came");
        return default;
    }
}
