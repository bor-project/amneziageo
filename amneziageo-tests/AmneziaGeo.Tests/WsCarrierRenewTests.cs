using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// How the carrier changes the websocket under a tunnel. A connection that went silent is not closed and dialled
/// again, which leaves a call without a path for the time of the dial: another one is opened beside it, both carry
/// what the tunnel sends, and the tunnel moves once the server answers through the new one.
/// </summary>
public sealed class WsCarrierRenewTests : IDisposable
{
    private const int WaitMs = 5000;

    private readonly Front _front = new();
    private readonly Socket _engine = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly ConcurrentQueue<string> _notes = new();
    private readonly WsCarrier _carrier;

    /// <summary>
    /// ctor
    /// </summary>
    public WsCarrierRenewTests()
    {
        _engine.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _carrier = WsCarrier.Start(
            new WsEndpoint("127.0.0.1", _front.Port, Offered: true), IPAddress.Loopback, 51820, () => "token", null,
            (message, _) => _notes.Enqueue(message));
        _carrier.TrialMs = 700;
        _carrier.RetireMs = 200;
    }

    [Fact]
    public void ADatagram_CrossesTheFrontAndItsAnswerComesBack()
    {
        Say("one");

        Assert.Equal("one", _front.Await(0).Take(WaitMs));
        Assert.Equal("one", Hear(WaitMs));
    }

    [Fact]
    public void Renew_MovesTheTunnelOnceTheServerAnswersThroughTheNewWebsocket()
    {
        var first = Carrying();
        first.Answers = false;

        Assert.True(_carrier.Renew());
        var second = Beside(1);

        Assert.StartsWith("probe", Hear(WaitMs));
        Assert.True(Until(() => Noted("the server answered through"), WaitMs), Notes());
        Drain(first);
        Say("after");
        Assert.Equal("after", second.Take(WaitMs));
        Assert.Null(first.Take(300));
        Assert.True(first.Ended.Wait(WaitMs), "the websocket the tunnel left was not closed");
        Assert.Equal(2, _front.Count);
    }

    [Fact]
    public void Renew_FeedsBothWebsocketsWhileTheNewOneIsOnTrial()
    {
        _carrier.TrialMs = 20000;
        var first = Carrying();
        first.Answers = false;
        _front.Answers = false;

        Assert.True(_carrier.Renew());
        var second = Beside(1);
        Drain(first);
        Drain(second);
        Say("both");

        Assert.Equal("both", first.Take(WaitMs));
        Assert.Equal("both", second.Take(WaitMs));
    }

    [Fact]
    public void Renew_LeavesTheTunnelWhereItIsWhenOnlyTheWebsocketInUseAnswers()
    {
        var first = Carrying();
        _front.Answers = false;

        Assert.True(_carrier.Renew());
        var second = Beside(1);
        for (var step = 0; step < 30 && !second.Ended.IsSet; step++)
        {
            Say("still");
            Thread.Sleep(50);
        }

        Assert.True(second.Ended.Wait(WaitMs), "the websocket that brought nothing was not closed");
        Assert.True(Until(() => Noted("still carries"), WaitMs), Notes());
        Silence();
        Say("later");
        Assert.Equal("later", Hear(WaitMs));
        Assert.False(first.Ended.IsSet);
    }

    [Fact]
    public void Renew_MovesTheTunnelWhenNeitherWebsocketAnswers()
    {
        var first = Carrying();
        first.Answers = false;
        _front.Answers = false;

        Assert.True(_carrier.Renew());
        var second = Beside(1);

        Assert.True(first.Ended.Wait(WaitMs), "the websocket that went silent was not closed");
        Assert.True(Until(() => Noted("neither websocket"), WaitMs), Notes());
        Drain(second);
        Say("later");
        Assert.Equal("later", second.Take(WaitMs));
    }

    [Fact]
    public void Renew_HandsTheTunnelOverAtOnceWhenTheWebsocketInUseEnds()
    {
        _carrier.TrialMs = 20000;
        var first = Carrying();
        _front.Answers = false;

        Assert.True(_carrier.Renew());
        var second = Beside(1);
        first.Close();

        Assert.True(Until(() => Noted("moves to the one opened beside it"), WaitMs), Notes());
        Drain(second);
        Say("after");
        Assert.Equal("after", second.Take(WaitMs));
        Thread.Sleep(1500);
        Assert.Equal(2, _front.Count);
    }

    [Fact]
    public void Renew_IsRefusedWithoutAWebsocketInUseAndWhileAnotherIsTried()
    {
        Assert.False(_carrier.Renew());

        _carrier.TrialMs = 20000;
        Carrying();
        _front.Answers = false;

        Assert.True(_carrier.Renew());
        Assert.False(_carrier.Renew());
    }

    [Fact]
    public void Renew_KeepsTheWebsocketInUseWhenTheFrontRefusesAnother()
    {
        var first = Carrying();
        _front.Refuses = true;

        Assert.True(_carrier.Renew());

        Assert.True(Until(() => Noted("so the tunnel stays on the one in use"), WaitMs), Notes());
        Silence();
        Say("on");
        Assert.Equal("on", Hear(WaitMs));
        Assert.False(first.Ended.IsSet);
        Assert.True(Until(_carrier.Renew, WaitMs), "another websocket could not be asked for after the refusal");
    }

    [Fact]
    public void Renew_MovesTheTunnelAtOnceWhenTheWebsocketInUseTakesNoMoreData()
    {
        _carrier.TrialMs = 20000;
        var first = Carrying();
        first.Answers = false;
        first.Reads = false;
        var burst = new byte[60000];
        var port = new IPEndPoint(IPAddress.Loopback, _carrier.LocalPort);
        for (var sent = 0; sent < 600; sent++)
        {
            _engine.SendTo(burst, port);
            if (sent % 4 == 3)
            {
                Thread.Sleep(2);
            }
        }

        Assert.True(_carrier.Renew());

        Assert.True(Until(() => Noted("so the tunnel moves to the one opened beside it"), 15000), Notes());
        Assert.True(Until(
            () =>
            {
                Say("probe");
                return Heard("probe", 100);
            },
            15000), "the tunnel did not carry through the websocket it moved to");
    }

    [Fact]
    public void Renew_ClosesTheNewWebsocketWhenItTakesNoData()
    {
        _carrier.TrialMs = 20000;
        var first = Carrying();
        _front.Answers = false;

        Assert.True(_carrier.Renew());
        var second = Beside(1);
        second.Reads = false;
        var burst = new byte[60000];
        var port = new IPEndPoint(IPAddress.Loopback, _carrier.LocalPort);
        for (var sent = 0; sent < 600; sent++)
        {
            _engine.SendTo(burst, port);
            if (sent % 4 == 3)
            {
                Thread.Sleep(2);
            }
        }

        Assert.True(Until(() => Noted("ms, so it is closed"), 15000), Notes());
        Assert.True(Until(
            () =>
            {
                Say("probe");
                return Heard("probe", 100);
            },
            15000), "the tunnel did not carry on through the websocket in use");
        Assert.False(first.Ended.IsSet);
    }

    [Fact]
    public void AWebsocketThatWasAnsweringSteadilyAndWentSilent_IsRenewedByTheCarrierItself()
    {
        Quick();
        var first = Carrying();
        Flow(400);
        first.Answers = false;

        Flow(1500, () => _front.Count == 2);

        Assert.Equal(2, _front.Count);
        Assert.True(Noted("has brought nothing for"), Notes());
        Flow(1500, () => Noted("the server answered through"));
        Assert.True(Noted("the server answered through"), Notes());
        Assert.True(first.Ended.Wait(WaitMs), "the websocket the tunnel left was not closed");
    }

    [Fact]
    public void AWebsocketThatWasNotAnsweringSteadily_IsNotRenewedByTheCarrier()
    {
        Quick();
        _carrier.SteadyMs = 2000;
        var first = Carrying();
        first.Answers = false;

        Flow(1200);

        Assert.Equal(1, _front.Count);
    }

    [Fact]
    public void ATunnelThatSendsLittleIntoTheSilence_IsNotRenewedByTheCarrier()
    {
        Quick();
        var first = Carrying();
        Flow(400);
        first.Answers = false;

        Thread.Sleep(600);
        for (var sent = 0; sent < 10; sent++)
        {
            Say("few");
            Thread.Sleep(20);
        }

        Thread.Sleep(600);
        Assert.Equal(1, _front.Count);
    }

    [Fact]
    public void TheCarrier_OpensAnotherWebsocketOnItsOwnOnlyOnceInAWhile()
    {
        Quick();
        _carrier.QuietHoldMs = 60000;
        var first = Carrying();
        Flow(400);
        first.Answers = false;
        Flow(1500, () => Noted("the server answered through"));
        Assert.True(Noted("the server answered through"), Notes());

        var second = _front.Await(1);
        Flow(400);
        second.Answers = false;
        Flow(1200);

        Assert.Equal(2, _front.Count);
    }

    [Fact]
    public void TheCarrier_TriesAgainSoonWhereNoOtherWebsocketCouldBeOpened()
    {
        Quick();
        _carrier.QuietHoldMs = 60000;
        _carrier.QuietRetryMs = 300;
        var first = Carrying();
        Flow(400);
        first.Answers = false;
        _front.Refuses = true;

        Flow(3000, () => _notes.Count(note => note.Contains("no other websocket could be opened", StringComparison.Ordinal)) >= 2);

        Assert.True(_notes.Count(note => note.Contains("no other websocket could be opened", StringComparison.Ordinal)) >= 2, Notes());
        Assert.False(first.Ended.IsSet);
    }

    [Fact]
    public void Dispose_ClosesTheWebsocketOnTrialToo()
    {
        _carrier.TrialMs = 20000;
        var first = Carrying();
        _front.Answers = false;

        Assert.True(_carrier.Renew());
        var second = Beside(1);
        _carrier.Dispose();

        Assert.True(first.Ended.Wait(WaitMs), "the websocket in use was left open");
        Assert.True(second.Ended.Wait(WaitMs), "the websocket on trial was left open");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _carrier.Dispose();
        _engine.Dispose();
        _front.Dispose();
    }

    // The first websocket, standing and proven by a datagram that went through and came back.
    private Connection Carrying()
    {
        Say("first");
        var connection = _front.Await(0);
        Assert.Equal("first", connection.Take(WaitMs));
        Assert.Equal("first", Hear(WaitMs));
        return connection;
    }

    // The websocket opened beside the one in use, once the carrier feeds it what the tunnel sends.
    private Connection Beside(int index)
    {
        var connection = _front.Await(index);
        Assert.True(
            Until(
                () =>
                {
                    Say("probe");
                    return connection.Take(50) is not null;
                },
                WaitMs),
            "the websocket opened beside the one in use was fed nothing: " + Notes());
        return connection;
    }

    // Shortens what the carrier waits for before it opens another websocket on its own.
    private void Quick()
    {
        _carrier.QuietMs = 300;
        _carrier.SteadyMs = 200;
    }

    // Sends a datagram every 20 ms for the time given, or until the condition holds.
    private void Flow(int ms, Func<bool>? until = null)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end && until?.Invoke() != true)
        {
            Say("flow");
            Thread.Sleep(20);
        }
    }

    // Sends a datagram of the engine to the carrier.
    private void Say(string text)
    {
        _engine.SendTo(Encoding.ASCII.GetBytes(text), new IPEndPoint(IPAddress.Loopback, _carrier.LocalPort));
    }

    // The next datagram the carrier hands back to the engine, null where none comes in time.
    private string? Hear(int ms)
    {
        if (!_engine.Poll(TimeSpan.FromMilliseconds(ms), SelectMode.SelectRead))
        {
            return null;
        }

        var buffer = new byte[65535];
        var read = _engine.Receive(buffer);
        return Encoding.ASCII.GetString(buffer, 0, Math.Min(read, 64));
    }

    // Whether a datagram that starts this way comes back in time, whatever else arrives before it.
    private bool Heard(string start, int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            if (Hear((int)Math.Max(1, until - Environment.TickCount64)) is { } text && text.StartsWith(start, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Drops the answers that are still on their way to the engine.
    private void Silence()
    {
        Thread.Sleep(200);
        while (Hear(50) is not null)
        {
        }
    }

    // Drops what a websocket has taken so far.
    private static void Drain(Connection connection)
    {
        Thread.Sleep(200);
        while (connection.Take(50) is not null)
        {
        }
    }

    private bool Noted(string part)
    {
        return _notes.Any(note => note.Contains(part, StringComparison.Ordinal));
    }

    private string Notes()
    {
        return string.Join(" | ", _notes);
    }

    private static bool Until(Func<bool> met, int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            if (met())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return met();
    }

    /// <summary>
    /// A websocket front on the loopback: takes the upgrade the carrier sends and plays the server behind it, which
    /// answers a datagram with the same datagram.
    /// </summary>
    private sealed class Front : IDisposable
    {
        private const string AcceptSalt = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private static readonly X509Certificate2 _certificate = Certificate();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<Connection> _connections = [];
        private volatile bool _disposed;

        /// <summary>
        /// ctor
        /// </summary>
        public Front()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            new Thread(Accept) { IsBackground = true, Name = "front" }.Start();
        }

        /// <summary>
        /// Port the front listens on.
        /// </summary>
        public int Port { get; }

        /// <summary>
        /// Whether the server behind the websockets opened from now on answers.
        /// </summary>
        public volatile bool Answers = true;

        /// <summary>
        /// Whether the upgrades asked for from now on are refused.
        /// </summary>
        public volatile bool Refuses;

        /// <summary>
        /// Websockets the front has opened so far.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_connections)
                {
                    return _connections.Count;
                }
            }
        }

        /// <summary>
        /// The websocket opened at this place in the order they came.
        /// </summary>
        public Connection Await(int index)
        {
            var until = Environment.TickCount64 + WaitMs;
            while (Environment.TickCount64 < until)
            {
                lock (_connections)
                {
                    if (_connections.Count > index)
                    {
                        return _connections[index];
                    }
                }

                Thread.Sleep(10);
            }

            throw new TimeoutException($"the front opened {Count} websocket(s), and number {index + 1} was awaited");
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _disposed = true;
            _listener.Stop();
            lock (_connections)
            {
                foreach (var connection in _connections)
                {
                    connection.Close();
                }
            }
        }

        private static X509Certificate2 Certificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=front.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
        }

        private void Accept()
        {
            while (!_disposed)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    new Thread(() => Serve(client)) { IsBackground = true, Name = "front websocket" }.Start();
                }
                catch (Exception) when (_disposed)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }
            }
        }

        // One connection of the carrier: TLS, the upgrade, then its datagrams.
        private void Serve(TcpClient client)
        {
            try
            {
                var tls = new SslStream(client.GetStream());
                tls.AuthenticateAsServer(_certificate);
                var request = Header(tls);
                var key = Regex.Match(request, "Sec-WebSocket-Key: (\\S+)").Groups[1].Value;
                if (Refuses)
                {
                    tls.Write(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"));
                    client.Close();
                    return;
                }

                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptSalt)));
                tls.Write(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
                var connection = new Connection(client, tls) { Answers = Answers };
                lock (_connections)
                {
                    _connections.Add(connection);
                }

                connection.Run();
            }
            catch (Exception) when (_disposed)
            {
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
            {
                client.Close();
            }
        }

        private static string Header(Stream stream)
        {
            var text = new StringBuilder();
            var one = new byte[1];
            while (!text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (stream.Read(one, 0, 1) <= 0)
                {
                    throw new IOException("the carrier closed the connection");
                }

                text.Append((char)one[0]);
            }

            return text.ToString();
        }
    }

    /// <summary>
    /// One websocket of the front, as the server behind it sees the tunnel.
    /// </summary>
    private sealed class Connection
    {
        private readonly TcpClient _client;
        private readonly Stream _stream;
        private readonly BlockingCollection<string> _taken = new();
        private readonly Lock _writing = new();

        /// <summary>
        /// ctor
        /// </summary>
        public Connection(TcpClient client, Stream stream)
        {
            _client = client;
            _stream = stream;
        }

        /// <summary>
        /// Whether the server answers a datagram that comes through this websocket.
        /// </summary>
        public volatile bool Answers;

        /// <summary>
        /// Whether the front still reads what the carrier writes into this websocket.
        /// </summary>
        public volatile bool Reads = true;

        /// <summary>
        /// Set once the websocket is closed, by either end.
        /// </summary>
        public ManualResetEventSlim Ended { get; } = new();

        /// <summary>
        /// The next datagram the carrier sent through this websocket, null where none comes in time.
        /// </summary>
        public string? Take(int ms)
        {
            return _taken.TryTake(out var text, ms) ? text : null;
        }

        /// <summary>
        /// Closes the websocket from the side of the front.
        /// </summary>
        public void Close()
        {
            _client.Close();
        }

        /// <summary>
        /// Reads the frames of the carrier until the websocket ends.
        /// </summary>
        public void Run()
        {
            try
            {
                var head = new byte[4];
                var payload = new byte[65535];
                while (true)
                {
                    while (!Reads)
                    {
                        Thread.Sleep(20);
                    }

                    Fill(head, 2);
                    var length = head[1] & 0x7f;
                    if (length == 126)
                    {
                        Fill(head, 2);
                        length = (head[0] << 8) | head[1];
                    }

                    Fill(payload, length);
                    _taken.Add(Encoding.ASCII.GetString(payload, 0, Math.Min(length, 64)));
                    if (Answers)
                    {
                        Write(payload.AsSpan(0, length));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
            }
            finally
            {
                _client.Close();
                Ended.Set();
            }
        }

        private void Fill(byte[] buffer, int count)
        {
            var read = 0;
            while (read < count)
            {
                var more = _stream.Read(buffer, read, count - read);
                if (more <= 0)
                {
                    throw new IOException("the carrier closed the websocket");
                }

                read += more;
            }
        }

        // One datagram of the server as a binary frame.
        private void Write(ReadOnlySpan<byte> datagram)
        {
            var frame = new byte[datagram.Length + 4];
            var used = WsCarrier.Encode(frame, datagram, 0x2);
            lock (_writing)
            {
                _stream.Write(frame, 0, used);
            }
        }
    }
}
