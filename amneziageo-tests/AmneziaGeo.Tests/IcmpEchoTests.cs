using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The echo confined to one path: nothing is sent unless the caller placed its socket there first.
/// </summary>
public sealed class IcmpEchoTests
{
    private const int TimeoutMs = 3000;

    // Well under the timeout: an echo that was not sent is not waited for.
    private const int AtOnceMs = 1000;

    private const string Why = "the echo leaves from a socket of linux and android";

    [UnixFact(Why)]
    public async Task AnEchoConfinedToAPath_ComesBack()
    {
        if (!Echoes())
        {
            return;
        }

        Assert.True(await IcmpEcho.ConfinedAsync(IPAddress.Loopback, TimeoutMs, _ => true, CancellationToken.None) >= 0);
    }

    [UnixFact(Why)]
    public async Task TheSocketIsConfinedBeforeItIsConnected()
    {
        if (!Echoes())
        {
            return;
        }

        var connected = new List<bool>();

        await IcmpEcho.ConfinedAsync(IPAddress.Loopback, TimeoutMs, socket => Seen(connected, socket), CancellationToken.None);

        Assert.Equal([false], connected);
    }

    [Fact]
    public async Task ASocketThatWasNotConfined_SendsNoEcho()
    {
        var clock = Stopwatch.StartNew();

        Assert.Equal(-1, await IcmpEcho.ConfinedAsync(IPAddress.Loopback, TimeoutMs, _ => false, CancellationToken.None));
        Assert.True(clock.ElapsedMilliseconds < AtOnceMs);
    }

    [Fact]
    public async Task AConfinementThatFails_SendsNoEcho()
    {
        Assert.Equal(-1, await IcmpEcho.ConfinedAsync(IPAddress.Loopback, TimeoutMs, _ => throw new InvalidOperationException("no path"), CancellationToken.None));
    }

    [Fact]
    public async Task ASessionThatEnded_SendsNoEcho()
    {
        using var ended = new CancellationTokenSource();
        ended.Cancel();
        var connected = new List<bool>();

        Assert.Equal(-1, await IcmpEcho.ConfinedAsync(IPAddress.Loopback, TimeoutMs, socket => Seen(connected, socket), ended.Token));
        Assert.Empty(connected);
    }

    // Notes whether the socket handed over for confinement is connected already.
    private static bool Seen(List<bool> connected, Socket socket)
    {
        connected.Add(socket.Connected);
        return true;
    }

    // Whether this system hands the user running the tests a socket for echoes.
    private static bool Echoes()
    {
        return Opens(SocketType.Dgram) || Opens(SocketType.Raw);
    }

    private static bool Opens(SocketType kind)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, kind, ProtocolType.Icmp);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
