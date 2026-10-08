using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A server of ours takes the tunnel of a device down by a signal: the hello names the port and the addresses the
/// signal comes from, the device listens at its own address inside the tunnel, and it takes the signal only sealed
/// under the keys of its config and only from the server.
/// </summary>
public sealed class ServerSignalTests
{
    [Fact]
    public void TheOffer_NamesThePortAndTheSourcesOfTheSignal()
    {
        var offer = ServerOffer.Parse(
            """{"server":"amneziageo","version":"1","client":"c","features":{"disconnect":{"port":28561,"from":["10.8.0.1","fd00::1","not an address",7]}}}""");
        var odd = ServerOffer.Parse(
            """{"server":"amneziageo","version":"1","client":"c","features":{"disconnect":{"port":70000,"from":"10.8.0.1"}}}""");

        Assert.Equal(28561, offer.SignalPort);
        Assert.Equal(["10.8.0.1", "fd00::1"], offer.SignalSources());
        Assert.Equal(0, odd.SignalPort);
        Assert.Empty(odd.SignalSources());
        Assert.Equal(0, ServerOffer.None.SignalPort);
        Assert.Empty(ServerOffer.None.SignalSources());
    }

    [Fact]
    public void ThePlace_IsTheAddressOfTheConfigInTheFamilyTheSignalComesOver()
    {
        var keys = Keys();
        var both = Offer("""["10.8.0.1","fd00::1"]""");
        var four = Offer("""["10.8.0.1"]""");

        var place = TunnelSignal.Of(both, Config(keys, "10.8.0.2/32, fd00::2/128"));
        var narrowed = TunnelSignal.Of(four, Config(keys, "10.8.0.2/32, fd00::2/128"));
        var withoutSix = TunnelSignal.Of(both, Config(keys, "10.8.0.2/32, fd00::2/128"), false);

        Assert.NotNull(place);
        Assert.Equal(["10.8.0.2", "fd00::2"], place.Hosts);
        Assert.Equal(["10.8.0.1", "fd00::1"], place.Sources);
        Assert.Equal(["10.8.0.1/32", "fd00::1/128"], place.Routes);
        Assert.Equal(28561, place.Port);
        Assert.Equal(keys.ClientPrivate, place.PrivateKey);
        Assert.Equal(keys.ServerPublic, place.ServerKey);
        Assert.Equal(["10.8.0.2"], narrowed!.Hosts);
        Assert.Equal(["10.8.0.2"], withoutSix!.Hosts);
        Assert.Equal(["10.8.0.1"], withoutSix.Sources);
        Assert.Null(TunnelSignal.Of(Offer("""["fd00::1"]"""), Config(keys, "10.8.0.2/32")));
        Assert.Null(TunnelSignal.Of(Offer("""["fd00::1"]"""), Config(keys, "10.8.0.2/32, fd00::2/128"), false));
        Assert.Null(TunnelSignal.Of(ServerOffer.None, Config(keys, "10.8.0.2/32")));
        Assert.Null(TunnelSignal.Of(null, Config(keys, "10.8.0.2/32")));
        Assert.Null(TunnelSignal.Of(both, "[Interface]\nAddress = 10.8.0.2/32\n"));
    }

    [Fact]
    public void ThePlace_IsTheSameFromThePortAndTheSourcesAlone()
    {
        var keys = Keys();
        var config = Config(keys, "10.8.0.2/32, fd00::2/128");

        var place = TunnelSignal.Of(28561, ["10.8.0.1", "fd00::1"], config);
        var withoutSix = TunnelSignal.Of(28561, ["10.8.0.1", "fd00::1"], config, false);

        Assert.NotNull(place);
        Assert.Equal(["10.8.0.2", "fd00::2"], place.Hosts);
        Assert.Equal(["10.8.0.1", "fd00::1"], place.Sources);
        Assert.Equal(28561, place.Port);
        Assert.Equal(keys.ClientPrivate, place.PrivateKey);
        Assert.Equal(keys.ServerPublic, place.ServerKey);
        Assert.Equal(["10.8.0.2"], withoutSix!.Hosts);
        Assert.Equal(["10.8.0.1"], withoutSix.Sources);
        Assert.Null(TunnelSignal.Of(0, ["10.8.0.1"], config));
        Assert.Null(TunnelSignal.Of(28561, [], config));
        Assert.Null(TunnelSignal.Of(28561, ["not an address"], config));
        Assert.Null(TunnelSignal.Of(28561, ["10.8.0.1"], "[Interface]\nAddress = 10.8.0.2/32\n"));
    }

    [Fact]
    public void ThePlace_KnowsTheAnswersOfItsListenerByTheirEnds()
    {
        var keys = Keys();
        var place = TunnelSignal.Of(28561, ["10.8.0.1", "fd00::1"], Config(keys, "10.8.0.2/32, fd00::2/128"))!;
        var device = 0x0A080002u;
        var server = 0x0A080001u;

        Assert.True(place.Answers(device, 28561, server));
        Assert.False(place.Answers(device, 28562, server));
        Assert.False(place.Answers(device, 28561, 0x0A080003u));
        Assert.False(place.Answers(0x0A080009u, 28561, server));
        Assert.False(place.Answers(server, 28561, device));
    }

    [Fact]
    public async Task ASignalSealedUnderTheKeysOfTheConfig_IsTakenAndAnswered()
    {
        var keys = Keys();
        var place = TunnelSignal.Of(Offer("""["10.8.0.1"]"""), Config(keys, "10.8.0.2/32"))!;
        var (device, server) = await PairAsync();
        using (device)
        using (server)
        {
            var taking = ServerSignal.TakeAsync(device.GetStream(), place, CancellationToken.None);
            var answered = await CallAsync(server.GetStream(), keys.ServerPrivate, keys.ClientPublic, ServerSignal.Word);

            Assert.True(await taking);
            Assert.True(answered);
        }
    }

    [Fact]
    public async Task ASignalSealedUnderOtherKeys_IsNotTaken()
    {
        var keys = Keys();
        var place = TunnelSignal.Of(Offer("""["10.8.0.1"]"""), Config(keys, "10.8.0.2/32"))!;
        var (device, server) = await PairAsync();
        using (device)
        using (server)
        {
            var taking = ServerSignal.TakeAsync(device.GetStream(), place, CancellationToken.None);
            var calling = CallAsync(server.GetStream(), Keys().ServerPrivate, keys.ClientPublic, ServerSignal.Word);

            Assert.False(await taking);
            device.Close();
            Assert.Null(await calling);
        }
    }

    [Fact]
    public async Task AnotherWord_IsNotTaken()
    {
        var keys = Keys();
        var place = TunnelSignal.Of(Offer("""["10.8.0.1"]"""), Config(keys, "10.8.0.2/32"))!;
        var (device, server) = await PairAsync();
        using (device)
        using (server)
        {
            var taking = ServerSignal.TakeAsync(device.GetStream(), place, CancellationToken.None);
            var calling = CallAsync(server.GetStream(), keys.ServerPrivate, keys.ClientPublic, "reboot");

            Assert.False(await taking);
            device.Close();
            Assert.Null(await calling);
        }
    }

    [Fact]
    public async Task WhatIsNotASealedLine_IsNotTaken()
    {
        var keys = Keys();
        var place = TunnelSignal.Of(Offer("""["10.8.0.1"]"""), Config(keys, "10.8.0.2/32"))!;
        var (device, server) = await PairAsync();
        using (device)
        using (server)
        {
            var taking = ServerSignal.TakeAsync(device.GetStream(), place, CancellationToken.None);
            await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\n\r\n"));

            Assert.False(await taking);
        }
    }

    [Fact]
    public async Task TheListener_TakesTheTunnelDownOnTheSignalOfTheServer()
    {
        var keys = Keys();
        var port = Free();
        var place = new SignalPlace([IPAddress.Loopback], port, [IPAddress.Loopback], keys.ClientPrivate, keys.ServerPublic);
        var down = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new CancellationTokenSource();
        var listening = ServerSignal.ListenAsync(place, () => { down.TrySetResult(); return Task.CompletedTask; }, null, session.Token);

        using var server = await DialAsync(IPAddress.Loopback, port);
        var answered = await CallAsync(server.GetStream(), keys.ServerPrivate, keys.ClientPublic, ServerSignal.Word);
        server.Close();
        await down.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.CancelAsync();
        await listening.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(answered);
    }

    [Fact]
    public async Task TheListener_TurnsAwayACallerThatIsNotTheServer()
    {
        var keys = Keys();
        var port = Free();
        var other = IPAddress.Parse("127.0.0.9");
        var place = new SignalPlace([IPAddress.Loopback], port, [other], keys.ClientPrivate, keys.ServerPublic);
        var downs = 0;
        var notes = new List<string>();
        using var session = new CancellationTokenSource();
        var listening = ServerSignal.ListenAsync(
            place,
            () => { Interlocked.Increment(ref downs); return Task.CompletedTask; },
            (message, _) => { lock (notes) { notes.Add(message); } },
            session.Token);

        using var stranger = await DialAsync(IPAddress.Loopback, port);
        var turned = await CallAsync(stranger.GetStream(), keys.ServerPrivate, keys.ClientPublic, ServerSignal.Word);
        await session.CancelAsync();
        await listening.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(turned);
        Assert.Equal(0, Volatile.Read(ref downs));
        lock (notes)
        {
            Assert.Contains(notes, note => note.Contains("turned away", StringComparison.Ordinal));
        }
    }

    private static ServerOffer Offer(string from) =>
        ServerOffer.Parse("""{"server":"amneziageo","version":"1","client":"c","features":{"disconnect":{"port":28561,"from":""" + from + "}}}");

    private static string Config((string ClientPrivate, string ClientPublic, string ServerPrivate, string ServerPublic) keys, string address) =>
        $"[Interface]\nPrivateKey = {keys.ClientPrivate}\nAddress = {address}\n\n[Peer]\nPublicKey = {keys.ServerPublic}\nEndpoint = vpn.example:51820\nAllowedIPs = 0.0.0.0/0\n";

    private static (string ClientPrivate, string ClientPublic, string ServerPrivate, string ServerPublic) Keys()
    {
        var client = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var server = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        return (client, Curve25519.PublicOf(client), server, Curve25519.PublicOf(server));
    }

    private static int Free()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    // Two ends of one connection on the loopback.
    private static async Task<(TcpClient Device, TcpClient Server)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var server = new TcpClient();
            await server.ConnectAsync((IPEndPoint)listener.LocalEndpoint);

            return (await listener.AcceptTcpClientAsync(), server);
        }
        finally
        {
            listener.Stop();
        }
    }

    // Dials the listener, which binds on its own time.
    private static async Task<TcpClient> DialAsync(IPAddress address, int port)
    {
        for (var attempt = 0; ; attempt++)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(address, port);

                return client;
            }
            catch (SocketException) when (attempt < 100)
            {
                client.Dispose();
                await Task.Delay(50);
            }
        }
    }

    // Says the signal the way the server does; true when the device answered that it took it, null when it hung up.
    private static async Task<bool?> CallAsync(NetworkStream stream, string serverPrivate, string clientPublic, string word)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
        try
        {
            if (await reader.ReadLineAsync() is not { } greeting)
            {
                return null;
            }

            using var hello = JsonDocument.Parse(greeting);
            var nonce = hello.RootElement.GetProperty("nonce").GetString()!;
            var body = Encoding.UTF8.GetBytes($"{{\"signal\":\"{word}\",\"taken\":false}}");
            var (iv, data) = ServiceToken.Seal(serverPrivate, clientPublic, nonce, body, ServiceToken.SignalContext);
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"{{\"iv\":\"{iv}\",\"data\":\"{data}\"}}\n"));
            if (await reader.ReadLineAsync() is not { } line)
            {
                return null;
            }

            using var answer = JsonDocument.Parse(line);
            var opened = ServiceToken.Open(
                serverPrivate,
                clientPublic,
                nonce,
                answer.RootElement.GetProperty("iv").GetString()!,
                answer.RootElement.GetProperty("data").GetString()!,
                ServiceToken.SignalContext);
            if (opened is null)
            {
                return false;
            }

            using var said = JsonDocument.Parse(opened);

            return said.RootElement.GetProperty("taken").GetBoolean()
                && said.RootElement.GetProperty("signal").GetString() == ServerSignal.Word;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
