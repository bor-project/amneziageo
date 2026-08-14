using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using AmneziaGeo.Decl;
using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The proxy the application offers on a fixed port has to speak what a neighbouring client already speaks:
/// SOCKS5 with and without a password, and HTTP in both its forms. What it decides is the outbound's business,
/// so here the destination is a plain echo and only the protocol is under test.
/// </summary>
public sealed class LocalProxyServerTests : IDisposable
{
    private readonly EchoDestination _destination = new();
    private readonly List<LocalProxyServer> _servers = [];
    private readonly List<Socket> _clients = [];

    [Fact]
    public async Task Socks5_WithoutAPassword_CarriesBytesToTheDestination()
    {
        var outbound = new TestOutbound(_destination);
        var options = Options();
        Listen(outbound, options);
        var client = await DialAsync(options.SocksPort);

        await client.SendAsync(new byte[] { 5, 1, 0 }, SocketFlags.None);
        Assert.Equal([5, 0], await ReadAsync(client, 2));
        await client.SendAsync(Request("example.test", 443), SocketFlags.None);
        var reply = await ReadAsync(client, 10);

        Assert.Equal(5, reply[0]);
        Assert.Equal(0, reply[1]);
        Assert.Equal("example.test", outbound.Host);
        Assert.Equal(443, outbound.Port);
        await client.SendAsync("ping"u8.ToArray(), SocketFlags.None);
        Assert.Equal("ping", Encoding.ASCII.GetString(await ReadAsync(client, 4)));
    }

    [Fact]
    public async Task Socks5_WithAPassword_TakesTheRightOneAndRefusesTheWrongOne()
    {
        var options = Secured("bor:secret");
        Listen(new TestOutbound(_destination), options);

        var wrong = await DialAsync(options.SocksPort);
        await wrong.SendAsync(new byte[] { 5, 1, 2 }, SocketFlags.None);
        Assert.Equal([5, 2], await ReadAsync(wrong, 2));
        await wrong.SendAsync(Credentials("bor", "guess"), SocketFlags.None);
        Assert.Equal([1, 1], await ReadAsync(wrong, 2));

        var right = await DialAsync(options.SocksPort);
        await right.SendAsync(new byte[] { 5, 1, 2 }, SocketFlags.None);
        Assert.Equal([5, 2], await ReadAsync(right, 2));
        await right.SendAsync(Credentials("bor", "secret"), SocketFlags.None);
        Assert.Equal([1, 0], await ReadAsync(right, 2));
        await right.SendAsync(Request("example.test", 80), SocketFlags.None);
        Assert.Equal(0, (await ReadAsync(right, 10))[1]);
    }

    [Fact]
    public async Task Socks5_WithoutTheMethodTheProxyAsksFor_IsTurnedAway()
    {
        var options = Secured("bor:secret");
        Listen(new TestOutbound(_destination), options);
        var client = await DialAsync(options.SocksPort);

        await client.SendAsync(new byte[] { 5, 1, 0 }, SocketFlags.None);

        Assert.Equal([5, 0xFF], await ReadAsync(client, 2));
    }

    [Fact]
    public async Task Socks5_AskingForUdp_IsToldTheCommandIsNotServed()
    {
        var options = Options();
        Listen(new TestOutbound(_destination), options);
        var client = await DialAsync(options.SocksPort);

        await client.SendAsync(new byte[] { 5, 1, 0 }, SocketFlags.None);
        await ReadAsync(client, 2);
        // UDP associate: the command byte is the only difference from a connect.
        await client.SendAsync(Request("example.test", 443, command: 3), SocketFlags.None);

        Assert.Equal(7, (await ReadAsync(client, 10))[1]);
    }

    [Fact]
    public async Task HttpConnect_EstablishesAndCarriesBytes()
    {
        var outbound = new TestOutbound(_destination);
        var options = Options();
        Listen(outbound, options);
        var client = await DialAsync(options.HttpPort);

        await SendTextAsync(client, "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n");
        var reply = Encoding.ASCII.GetString(await ReadAsync(client, 39));

        Assert.StartsWith("HTTP/1.1 200", reply, StringComparison.Ordinal);
        Assert.Equal("example.test", outbound.Host);
        Assert.Equal(443, outbound.Port);
        await client.SendAsync("ping"u8.ToArray(), SocketFlags.None);
        Assert.Equal("ping", Encoding.ASCII.GetString(await ReadAsync(client, 4)));
    }

    [Fact]
    public async Task HttpRequest_ReachesTheDestinationInOriginForm()
    {
        var outbound = new TestOutbound(_destination);
        var options = Options();
        Listen(outbound, options);
        var client = await DialAsync(options.HttpPort);

        await SendTextAsync(client, "GET http://example.test/path HTTP/1.1\r\nHost: example.test\r\n"
            + "Proxy-Connection: keep-alive\r\n\r\n");
        var head = await ReadHeadAsync(client);

        Assert.StartsWith("GET /path HTTP/1.1\r\n", head, StringComparison.Ordinal);
        Assert.Contains("Host: example.test\r\n", head, StringComparison.Ordinal);
        Assert.Contains("Connection: close\r\n", head, StringComparison.Ordinal);
        Assert.DoesNotContain("Proxy-Connection", head, StringComparison.Ordinal);
        Assert.Equal(80, outbound.Port);
    }

    [Fact]
    public async Task Http_WithAPassword_AsksForCredentialsAndThenTakesThem()
    {
        var options = Secured("bor:secret");
        Listen(new TestOutbound(_destination), options);

        var bare = await DialAsync(options.HttpPort);
        await SendTextAsync(bare, "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n");
        Assert.Contains("407", Encoding.ASCII.GetString(await ReadAsync(bare, 32)), StringComparison.Ordinal);

        var authorized = await DialAsync(options.HttpPort);
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("bor:secret"));
        await SendTextAsync(authorized, "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n"
            + $"Proxy-Authorization: Basic {basic}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(await ReadAsync(authorized, 12)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABlockedDestination_IsRefusedInBothFronts()
    {
        var options = Options();
        Listen(new TestOutbound(_destination, ProxyOutcome.Blocked), options);

        var socks = await DialAsync(options.SocksPort);
        await socks.SendAsync(new byte[] { 5, 1, 0 }, SocketFlags.None);
        await ReadAsync(socks, 2);
        await socks.SendAsync(Request("blocked.test", 443), SocketFlags.None);
        Assert.Equal(2, (await ReadAsync(socks, 10))[1]);

        var http = await DialAsync(options.HttpPort);
        await SendTextAsync(http, "CONNECT blocked.test:443 HTTP/1.1\r\nHost: blocked.test:443\r\n\r\n");
        Assert.Contains("403", Encoding.ASCII.GetString(await ReadAsync(http, 24)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Socks5_WithSeveralAccounts_TakesEveryOneOfThem()
    {
        var options = Secured("bor:secret\nguest:letmein");
        Listen(new TestOutbound(_destination), options);

        foreach (var (user, password) in new[] { ("bor", "secret"), ("guest", "letmein") })
        {
            var client = await DialAsync(options.SocksPort);
            await client.SendAsync(new byte[] { 5, 1, 2 }, SocketFlags.None);
            Assert.Equal([5, 2], await ReadAsync(client, 2));
            await client.SendAsync(Credentials(user, password), SocketFlags.None);
            Assert.Equal([1, 0], await ReadAsync(client, 2));
        }

        var crossed = await DialAsync(options.SocksPort);
        await crossed.SendAsync(new byte[] { 5, 1, 2 }, SocketFlags.None);
        await ReadAsync(crossed, 2);
        await crossed.SendAsync(Credentials("bor", "letmein"), SocketFlags.None);
        Assert.Equal([1, 1], await ReadAsync(crossed, 2));
    }

    [Fact]
    public async Task Http_TakesTheSecondAccountAsReadilyAsTheFirst()
    {
        var options = Secured("bor:secret\nguest:letmein");
        Listen(new TestOutbound(_destination), options);
        var client = await DialAsync(options.HttpPort);

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("guest:letmein"));
        await SendTextAsync(client, "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n"
            + $"Proxy-Authorization: Basic {basic}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(await ReadAsync(client, 12)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConnectedClient_IsListedWhileItHoldsTheConnection()
    {
        var options = Options();
        var server = Listen(new TestOutbound(_destination), options);
        var client = await DialAsync(options.SocksPort);

        await client.SendAsync(new byte[] { 5, 1, 0 }, SocketFlags.None);
        await ReadAsync(client, 2);
        var peers = server.Peers();

        Assert.Single(peers);
        Assert.Equal(IPAddress.Loopback.ToString(), peers[0].Address);
        Assert.Equal(((IPEndPoint)client.LocalEndPoint!).Port, peers[0].Port);
    }

    [Fact]
    public void TheAddressOffered_IsARoutedLinkOfThisMachineAndNeverTheTunnels()
    {
        var lan = new LocalProxyServer.AdapterView(NetworkInterfaceType.Ethernet, true, [IPAddress.Parse("10.0.110.22")]);
        // A virtual switch of a hypervisor reports itself as ethernet and keeps its segment to this machine.
        var host = new LocalProxyServer.AdapterView(NetworkInterfaceType.Ethernet, false, [IPAddress.Parse("172.23.144.1")]);
        // A WireGuard adapter reports itself as a proprietary virtual link (ifType 53), a dial-up as PPP.
        var tunnel = new LocalProxyServer.AdapterView((NetworkInterfaceType)53, false, [IPAddress.Parse("10.8.2.29")]);
        var dialup = new LocalProxyServer.AdapterView(NetworkInterfaceType.Ppp, false, [IPAddress.Parse("172.16.3.50")]);

        var usable = LocalProxyServer.Usable([tunnel, host, dialup, lan]);

        Assert.Equal(["10.0.110.22"], usable);
    }

    [Fact]
    public void WithNoLinkRouted_TheAddressesOfThisMachineAreOfferedAnyway()
    {
        var wifi = new LocalProxyServer.AdapterView(NetworkInterfaceType.Wireless80211, false, [IPAddress.Parse("192.168.1.47")]);
        var wired = new LocalProxyServer.AdapterView(NetworkInterfaceType.Ethernet, false, [IPAddress.Parse("192.168.137.1")]);

        var usable = LocalProxyServer.Usable([wifi, wired]);

        Assert.Equal(["192.168.1.47", "192.168.137.1"], usable);
    }

    [Fact]
    public void APublicAddress_IsNeverOffered()
    {
        var wan = new LocalProxyServer.AdapterView(NetworkInterfaceType.Ethernet, true, [IPAddress.Parse("46.8.237.222")]);

        Assert.Empty(LocalProxyServer.Usable([wan]));
    }

    [Fact]
    public void AccountsSurviveTheTextTheyAreStoredIn()
    {
        var accounts = new[] { new ProxyAccount("bor", "se:cret"), new ProxyAccount(" guest ", "letmein") };

        var text = ProxyCredentials.Compose(accounts);
        var read = ProxyCredentials.Parse(text);

        Assert.Equal(2, read.Count);
        Assert.Equal("bor", read[0].User);
        Assert.Equal("se:cret", read[0].Password);
        Assert.Equal("guest", read[1].User);
        Assert.Empty(ProxyCredentials.Parse("nameless"));
    }

    [Fact]
    public void AnAccountWithoutAName_IsNotStored()
    {
        Assert.Empty(ProxyCredentials.Compose([new ProxyAccount("  ", "secret")]));
        Assert.True(new LocalProxyOptions().RequiresAuth);
        Assert.False((new LocalProxyOptions { AllowAnonymous = true }).RequiresAuth);
    }

    [Fact]
    public void WithAPasswordAskedForAndNoAccount_TheSettingsAdmitNobody()
    {
        Assert.True(new LocalProxyOptions().AdmitsNobody);
        Assert.False((new LocalProxyOptions { AllowAnonymous = true }).AdmitsNobody);
        Assert.False((new LocalProxyOptions { Credentials = "bor:secret" }).AdmitsNobody);
    }

    [Fact]
    public async Task AnonymousAndAnAccount_BothGetIn()
    {
        var options = Options() with { Credentials = "bor:secret" };
        Listen(new TestOutbound(_destination), options);

        var bare = await DialAsync(options.SocksPort);
        await bare.SendAsync(new byte[] { 5, 1, 0 }, SocketFlags.None);
        Assert.Equal([5, 0], await ReadAsync(bare, 2));

        var named = await DialAsync(options.SocksPort);
        await named.SendAsync(new byte[] { 5, 1, 2 }, SocketFlags.None);
        Assert.Equal([5, 2], await ReadAsync(named, 2));
        await named.SendAsync(Credentials("bor", "secret"), SocketFlags.None);
        Assert.Equal([1, 0], await ReadAsync(named, 2));

        var http = await DialAsync(options.HttpPort);
        await SendTextAsync(http, "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(await ReadAsync(http, 12)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoAccountAndNoAnonymousAccess_NobodyIsAdmitted()
    {
        var options = Options() with { AllowAnonymous = false };
        Listen(new TestOutbound(_destination), options);

        var client = await DialAsync(options.SocksPort);
        await client.SendAsync(new byte[] { 5, 2, 0, 2 }, SocketFlags.None);

        Assert.Equal([5, 0xFF], await ReadAsync(client, 2));
    }

    [Fact]
    public void OnePortForBothFronts_IsBoundOnce()
    {
        var port = FreePort();
        var options = new LocalProxyOptions { Enabled = true, SocksPort = port, HttpPort = port };

        Assert.Single(options.Ports);
        Listen(new TestOutbound(_destination), options);
    }

    [Fact]
    public void APortAlreadyTaken_IsReportedInsteadOfListening()
    {
        var options = Options();
        Listen(new TestOutbound(_destination), options);
        var second = new LocalProxyServer(new TestOutbound(_destination), _ => { });
        _servers.Add(second);

        Assert.False(second.Apply(options));
        Assert.False(second.Running);
        Assert.Contains("taken", second.Error, StringComparison.Ordinal);
    }

    private LocalProxyServer Listen(IProxyOutbound outbound, LocalProxyOptions options)
    {
        var server = new LocalProxyServer(outbound, _ => { });
        _servers.Add(server);
        Assert.True(server.Apply(options));
        Assert.True(server.Running);
        return server;
    }

    private LocalProxyOptions Options()
    {
        return new LocalProxyOptions { Enabled = true, AllowAnonymous = true, SocksPort = FreePort(), HttpPort = FreePort() };
    }

    // The same listener with a password asked for.
    private LocalProxyOptions Secured(string credentials)
    {
        return Options() with { AllowAnonymous = false, Credentials = credentials };
    }

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return probe.LocalEndPoint is IPEndPoint bound ? bound.Port : 0;
    }

    // A SOCKS5 request naming the destination by name, which is what a client that leaves resolution to the
    // proxy sends.
    private static byte[] Request(string host, int port, byte command = 1)
    {
        var name = Encoding.ASCII.GetBytes(host);
        var request = new byte[7 + name.Length];
        request[0] = 5;
        request[1] = command;
        request[3] = 3;
        request[4] = (byte)name.Length;
        name.CopyTo(request, 5);
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)(port & 0xFF);
        return request;
    }

    private static byte[] Credentials(string user, string password)
    {
        var name = Encoding.UTF8.GetBytes(user);
        var secret = Encoding.UTF8.GetBytes(password);
        var message = new byte[3 + name.Length + secret.Length];
        message[0] = 1;
        message[1] = (byte)name.Length;
        name.CopyTo(message, 2);
        message[2 + name.Length] = (byte)secret.Length;
        secret.CopyTo(message, 3 + name.Length);
        return message;
    }

    private async Task<Socket> DialAsync(int port)
    {
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        _clients.Add(client);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        return client;
    }

    private static async Task SendTextAsync(Socket socket, string text)
    {
        await socket.SendAsync(Encoding.ASCII.GetBytes(text), SocketFlags.None);
    }

    // Reads until the head the destination echoed back is whole.
    private static async Task<string> ReadHeadAsync(Socket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[512];
        var text = new StringBuilder();
        while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
            if (read <= 0)
            {
                break;
            }

            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return text.ToString();
    }

    private static async Task<byte[]> ReadAsync(Socket socket, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[count];
        var used = 0;
        while (used < count)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(used), SocketFlags.None, timeout.Token);
            if (read <= 0)
            {
                break;
            }

            used += read;
        }

        return buffer[..used];
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        foreach (var server in _servers)
        {
            server.Dispose();
        }

        _destination.Dispose();
    }

    /// <summary>
    /// Destination that sends back whatever reaches it, so what the proxy wrote can be read from the client side.
    /// </summary>
    private sealed class EchoDestination : IDisposable
    {
        private readonly Socket _listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly CancellationTokenSource _cts = new();

        /// <summary>
        /// ctor
        /// </summary>
        public EchoDestination()
        {
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Listen(16);
            EndPoint = (IPEndPoint)_listener.LocalEndPoint!;
            _ = Task.Run(AcceptAsync);
        }

        /// <summary>
        /// Where the outbound connects.
        /// </summary>
        public IPEndPoint EndPoint { get; }

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptAsync(_cts.Token);
                    _ = Task.Run(() => EchoAsync(client));
                }
                catch (Exception)
                {
                    return;
                }
            }
        }

        private async Task EchoAsync(Socket client)
        {
            var buffer = new byte[8192];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var read = await client.ReceiveAsync(buffer, SocketFlags.None, _cts.Token);
                    if (read <= 0)
                    {
                        return;
                    }

                    await client.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, _cts.Token);
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _cts.Cancel();
            _listener.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Outbound that answers with the verdict the test names and otherwise opens the echo destination.
    /// </summary>
    private sealed class TestOutbound(EchoDestination destination, ProxyOutcome outcome = ProxyOutcome.Ok) : IProxyOutbound
    {
        /// <summary>
        /// Destination of the last request.
        /// </summary>
        public string Host { get; private set; } = string.Empty;

        /// <summary>
        /// Port of the last request.
        /// </summary>
        public int Port { get; private set; }

        /// <inheritdoc/>
        public async Task<(IProxyLink? Link, ProxyOutcome Outcome)> ConnectAsync(string host, int port, CancellationToken ct)
        {
            Host = host;
            Port = port;
            if (outcome != ProxyOutcome.Ok)
            {
                return (null, outcome);
            }

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(destination.EndPoint, ct);
            return (new TestLink(socket), ProxyOutcome.Ok);
        }
    }

    /// <summary>
    /// Open destination of the test outbound.
    /// </summary>
    private sealed class TestLink(Socket socket) : IProxyLink
    {
        /// <inheritdoc/>
        public Socket Socket { get; } = socket;

        /// <inheritdoc/>
        public void Count(int bytes)
        {
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Socket.Dispose();
        }
    }
}
