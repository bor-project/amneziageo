using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// While the tunnel of a config carries, the hello goes inside it, plain, to where the server said it answers there:
/// the answer is sealed by the keys of the config, so no certificate stands for anything. A server silent there, one
/// that named no such place and a config whose tunnel is down are asked past the tunnel as before.
/// </summary>
public sealed class HelloInsideTests : IAsyncLifetime
{
    private const string Text = "[Interface]\nPrivateKey = a\n\n[Peer]\nPublicKey = b\nEndpoint = vpn.example:51820\n";

    private const string Origin = "http://10.8.0.1:51820";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-inside-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _store.ClearPool();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AServerThatAnswersInside_IsAskedThereWhileTheTunnelCarries()
    {
        var past = 0;
        var inside = new List<ServiceTarget>();
        var offers = new ServerOffers(
            _store,
            ask: (_, _) => Task.FromResult(new HelloReply(Offer(8446, Origin), Interlocked.Increment(ref past) > 0)),
            carried: config => config == "office",
            askInside: (point, _) =>
            {
                inside.Add(point);

                return Task.FromResult(new HelloReply(Offer(9446, Origin), true));
            });

        await offers.AskAsync("office", Text, CancellationToken.None);
        var offer = await offers.AskAsync("office", Text, CancellationToken.None);

        Assert.Equal(1, past);
        Assert.Equal([Origin], inside.Select(point => point.Origin));
        Assert.Equal(9446, offer.WebSocketPort);
        Assert.Equal(9446, (await ServerOfferStore.KeptAsync(_store, "office", Text))!.Offer.WebSocketPort);
    }

    [Fact]
    public async Task AServerSilentInside_IsAskedPastTheTunnel()
    {
        var past = 0;
        var offers = new ServerOffers(
            _store,
            ask: (_, _) => Task.FromResult(new HelloReply(Offer(8446 + Interlocked.Increment(ref past), Origin), true)),
            carried: _ => true,
            askInside: (_, _) => Task.FromResult(new HelloReply(null, false)));

        await offers.AskAsync("office", Text, CancellationToken.None);
        var offer = await offers.AskAsync("office", Text, CancellationToken.None);

        Assert.Equal(2, past);
        Assert.Equal(8448, offer.WebSocketPort);
    }

    [Fact]
    public async Task AConfigWhoseTunnelIsDown_IsAskedPastTheTunnel()
    {
        var past = 0;
        var inside = 0;
        var offers = new ServerOffers(
            _store,
            ask: (_, _) => Task.FromResult(new HelloReply(Offer(8446 + Interlocked.Increment(ref past), Origin), true)),
            carried: config => config == "home",
            askInside: (_, _) => Task.FromResult(new HelloReply(Offer(9446, Origin), Interlocked.Increment(ref inside) > 0)));

        await offers.AskAsync("office", Text, CancellationToken.None);
        await offers.AskAsync("office", Text, CancellationToken.None);

        Assert.Equal(2, past);
        Assert.Equal(0, inside);
    }

    [Fact]
    public async Task AServerThatNamedNoPlaceInside_IsAskedPastTheTunnel()
    {
        var past = 0;
        var inside = 0;
        var offers = new ServerOffers(
            _store,
            ask: (_, _) => Task.FromResult(new HelloReply(Offer(8446 + Interlocked.Increment(ref past), null), true)),
            carried: _ => true,
            askInside: (_, _) => Task.FromResult(new HelloReply(Offer(9446, Origin), Interlocked.Increment(ref inside) > 0)));

        await offers.AskAsync("office", Text, CancellationToken.None);
        await offers.AskAsync("office", Text, CancellationToken.None);

        Assert.Equal(2, past);
        Assert.Equal(0, inside);
    }

    [Theory]
    [InlineData(Origin, Origin)]
    [InlineData("http://10.8.0.1:51820/api/hello", Origin)]
    [InlineData("http://[fd00::1]:443", "http://[fd00::1]:443")]
    [InlineData("https://10.8.0.1:51820", "")]
    [InlineData("http://vpn.example:51820", "")]
    [InlineData("10.8.0.1:51820", "")]
    public void TheOffer_NamesAPlainAddressInsideAlone(string named, string taken)
    {
        Assert.Equal(taken, Offer(8446, named).Inside());
    }

    [Fact]
    public void TheHelloInside_GoesToWhereTheServerNamed()
    {
        var point = new ServiceTarget("vpn.example", 51820, Key(), Key());

        Assert.Equal("https://vpn.example:51820", ServerHello.Origin(point));
        Assert.Equal(Origin, ServerHello.Origin(point with { Origin = Origin }));
        Assert.Equal(point.Mark(), (point with { Origin = Origin }).Mark());
    }

    [Fact]
    public async Task TheHelloInside_TravelsPlain()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = ServeOnceAsync(listener);

        var reply = await ServerHello.AskAsync(
            new ServiceTarget("vpn.example", 51820, Key(), Key()) { Origin = $"http://127.0.0.1:{port}" },
            CancellationToken.None);

        Assert.True(reply.Heard);
        Assert.Null(reply.Offer);
        Assert.StartsWith("POST /api/hello HTTP/1.1", await served, StringComparison.Ordinal);
    }

    // Takes one request and answers it with a page the services do not know.
    private static async Task<string> ServeOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer);
        await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());

        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private static ServerOffer Offer(int port, string? origin)
    {
        var inside = origin is null ? string.Empty : $",\"inside\":{{\"origin\":\"{origin}\"}}";

        return ServerOffer.Parse($"{{\"server\":\"amneziageo\",\"version\":\"1\",\"client\":\"c\",\"features\":{{\"websocket\":{{\"port\":{port}}}{inside}}}}}");
    }

    private static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
