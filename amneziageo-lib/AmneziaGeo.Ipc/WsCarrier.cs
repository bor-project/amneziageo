using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Ipc;

/// <summary>
/// How a websocket front answered an attempt to open a tunnel through it.
/// </summary>
public enum WsFrontOutcome
{
    /// <summary>
    /// The front accepted the upgrade.
    /// </summary>
    Ok,

    /// <summary>
    /// The front's name carries no address, or it does not resolve.
    /// </summary>
    NoAddress,

    /// <summary>
    /// Nothing answered before the timeout, or the connection was refused.
    /// </summary>
    NoAnswer,

    /// <summary>
    /// TLS did not come up.
    /// </summary>
    Tls,

    /// <summary>
    /// The front answered and refused the upgrade.
    /// </summary>
    Refused,
}

/// <summary>
/// Carries a tunnel's UDP inside a websocket to a wstunnel front, so a network that passes nothing but web
/// traffic still carries the tunnel. The engine dials the loopback port this binds, and every datagram travels
/// as one websocket message. The carrier opens on the first datagram and reopens itself after a drop, which
/// costs nothing extra: the engine repeats an unanswered handshake on its own. A websocket that went silent is
/// not closed first: another one is opened beside it and the tunnel moves once the server answers through it.
/// The carrier does that on its own where the server was answering steadily and stopped while the tunnel keeps
/// sending. Each way has a thread of its own that waits on its socket, so a datagram wakes one thread.
/// </summary>
public sealed class WsCarrier : IDisposable
{
    // Where the front hands the tunnel on the server: wstunnel forwards to its own loopback, and the port is
    // the one the config named before the carrier took its place.
    private const string TargetHost = "127.0.0.1";

    // Path the front serves the upgrade on, under the prefix a config may set as a shared secret.
    private const string DefaultPrefix = "v1";
    private const string UpgradePath = "events";
    private const string ProtocolToken = "v1";
    private const string BearerPrefix = "authorization.bearer.";
    private const string AcceptSalt = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private const int MaxDatagram = 65535;
    private const int FrameOverhead = 4;
    private const int MaxFrameHeader = 14;

    // What a control frame's payload may be, which the protocol keeps under this on its own.
    private const int ControlBytes = 125;
    private const int MaxHeaderBytes = 8192;
    private const int ConnectTimeoutMs = 8000;
    private const int RetryGapMs = 1000;
    private const int LoopbackProbeMs = 300;

    // How long one write to the front may take before the websocket counts as gone.
    private const int WriteTimeoutMs = 15000;

    // How long the front may say nothing at all before the websocket counts as gone: it pings on its own every
    // half minute, so silence this long is a connection that stands in name only.
    private const int SilenceMs = 75000;

    // How long a write to a websocket may stand before it counts as taking no more data, while one is on trial
    // beside the one in use.
    private const int BlockedMs = 500;

    // How often a trial looks at the two websockets.
    private const int TrialStepMs = 50;

    // How many times the tunnel sends into the silence of a websocket before the carrier looks how long it lasts.
    private const int QuietSent = 20;

    // The longest pause between two datagrams of the server that still counts as answering steadily.
    private const int SteadyGapMs = 1000;

    private const byte OpBinary = 0x2;
    private const byte OpClose = 0x8;
    private const byte OpPing = 0x9;
    private const byte OpPong = 0xa;
    private const byte FinalBit = 0x80;
    private const byte MaskBit = 0x80;

    // The front reads the token without checking its signature, and the reference client signs it with a key it
    // makes up at every start; this keeps that shape.
    private static readonly byte[] Secret = RandomNumberGenerator.GetBytes(32);

    private readonly WsEndpoint _front;
    private readonly IPAddress _address;
    private readonly int _targetPort;
    private readonly Func<string>? _token;
    private readonly Func<Socket, bool>? _bypass;
    private readonly Action<string, Exception?>? _note;
    private readonly Socket _local;
    private readonly byte[] _outgoing = new byte[MaxDatagram + FrameOverhead];
    private readonly SocketAddress _from = new(AddressFamily.InterNetwork);
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _sending = new();
    private readonly Lock _gate = new();
    private Stream? _stream;
    private Stream? _spare;
    private Stream? _retiring;
    private SocketAddress? _engine;
    private long _attempted;
    private long _heard;
    private long _answered;
    private long _steadySince;
    private long _watched;
    private long _writing;
    private long _offering;
    private int _unanswered;
    private int _renewing;
    private volatile bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    private WsCarrier(
        WsEndpoint front, IPAddress address, int targetPort, Func<string>? token, Func<Socket, bool>? bypass, Action<string, Exception?>? note)
    {
        _front = front;
        _address = address;
        _targetPort = targetPort;
        _token = token;
        _bypass = bypass;
        _note = note;
        _local = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _local.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        LocalPort = ((IPEndPoint)_local.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Loopback port the engine dials instead of the endpoint the network refuses to carry.
    /// </summary>
    public int LocalPort { get; }

    /// <summary>
    /// Milliseconds a websocket opened beside the one in use is tried before the choice between the two is made.
    /// </summary>
    internal int TrialMs { get; set; } = 8000;

    /// <summary>
    /// Milliseconds a websocket the tunnel has left is still heard, so what was on its way through it arrives.
    /// </summary>
    internal int RetireMs { get; set; } = 2000;

    /// <summary>
    /// Milliseconds a websocket that was answering steadily may bring nothing, while the tunnel keeps sending,
    /// before the carrier opens another one beside it.
    /// </summary>
    internal int QuietMs { get; set; } = 2000;

    /// <summary>
    /// Milliseconds the server has to answer without a pause for a websocket to count as answering steadily.
    /// </summary>
    internal int SteadyMs { get; set; } = 3000;

    /// <summary>
    /// Milliseconds before the carrier opens another websocket on its own again.
    /// </summary>
    internal int QuietHoldMs { get; set; } = 30000;

    /// <summary>
    /// Milliseconds before the carrier tries again where no other websocket could be opened.
    /// </summary>
    internal int QuietRetryMs { get; set; } = 3000;

    /// <summary>
    /// Binds the loopback port and starts carrying datagrams. The front is dialled at an address resolved by the
    /// caller, because a lookup made after the tunnel is built would travel inside it and answer nothing. Every
    /// upgrade carries a fresh header of the token the front of a server of ours asks for.
    /// </summary>
    public static WsCarrier Start(
        WsEndpoint front, IPAddress address, int targetPort, Func<string>? token, Func<Socket, bool>? bypass, Action<string, Exception?>? note)
    {
        var carrier = new WsCarrier(front, address, targetPort, token, bypass, note);
        Run(carrier.Pump, "ws carrier out");
        return carrier;
    }

    /// <summary>
    /// Whether a datagram crosses the loopback. The engine hands the carrier every packet on 127.0.0.1, so a
    /// firewall that drops UDP there leaves the carrier nothing to carry.
    /// </summary>
    public static bool LoopbackCarries()
    {
        try
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sender.SendTo(new byte[1], listener.LocalEndPoint!);
            return listener.Poll(TimeSpan.FromMilliseconds(LoopbackProbeMs), SelectMode.SelectRead);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// The upgrade request the front expects: what to open travels as a token in the protocol header, and the
    /// keys of the config are proven in the authorization header, else the account of the front.
    /// </summary>
    internal static string Handshake(WsEndpoint front, int targetPort, string key, string? authorization)
    {
        var prefix = front.PathPrefix.Length > 0 ? front.PathPrefix : DefaultPrefix;
        var request = new StringBuilder();
        request.Append($"GET /{prefix}/{UpgradePath} HTTP/1.1\r\n");
        request.Append($"Host: {front.Host}:{front.Port}\r\n");
        request.Append("Upgrade: websocket\r\n");
        request.Append("Connection: Upgrade\r\n");
        request.Append($"Sec-WebSocket-Key: {key}\r\n");
        request.Append("Sec-WebSocket-Version: 13\r\n");
        request.Append($"Sec-WebSocket-Protocol: {ProtocolToken}, {BearerPrefix}{Token(targetPort)}\r\n");
        if (authorization is not null)
        {
            request.Append($"Authorization: {authorization}\r\n");
        }
        else if (front.Credentials.Length > 0)
        {
            request.Append($"Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(front.Credentials))}\r\n");
        }

        request.Append("\r\n");
        return request.ToString();
    }

    // The tunnel to open, as the front reads it: a UDP forward to the server's own loopback with no idle timeout.
    private static string Token(int targetPort)
    {
        var header = Web("""{"typ":"JWT","alg":"HS256"}"""u8);
        var claims = Web(Encoding.UTF8.GetBytes(
            $"{{\"id\":\"{Guid.NewGuid()}\",\"p\":{{\"Udp\":{{\"timeout\":null}}}},\"r\":\"{TargetHost}\",\"rp\":{targetPort}}}"));
        var signed = Web(HMACSHA256.HashData(Secret, Encoding.ASCII.GetBytes($"{header}.{claims}")));
        return $"{header}.{claims}.{signed}";
    }

    private static string Web(ReadOnlySpan<byte> value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Writes one message into the frame buffer and returns its length. Nothing is masked: the front takes the
    /// payload as it stands and hands it to the server, so a masked datagram arrives there as noise.
    /// </summary>
    internal static int Encode(Span<byte> frame, ReadOnlySpan<byte> payload, byte opcode)
    {
        var header = payload.Length < 126 ? 2 : 4;
        frame[0] = (byte)(FinalBit | opcode);
        if (payload.Length < 126)
        {
            frame[1] = (byte)payload.Length;
        }
        else
        {
            frame[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(frame[2..], (ushort)payload.Length);
        }

        payload.CopyTo(frame[header..]);
        return header + payload.Length;
    }

    // Starts a thread of the carrier's own under the name given.
    private static void Run(ThreadStart body, string name)
    {
        new Thread(body) { IsBackground = true, Name = name }.Start();
    }

    // Datagrams from the engine, each one a message on the front.
    private void Pump()
    {
        while (!_disposed)
        {
            var stream = default(Stream);
            try
            {
                var used = Fill();
                stream = Ready();
                if (stream is null)
                {
                    continue;
                }

                if (Environment.TickCount64 - Volatile.Read(ref _heard) > SilenceMs)
                {
                    _note?.Invoke($"nothing has come back from {_front.Host}:{_front.Port} for {SilenceMs / 1000} s, so the websocket is opened again", null);
                    Drop(stream, null);
                    continue;
                }

                var frame = _outgoing.AsSpan(0, used);
                if (Volatile.Read(ref _spare) is { } spare)
                {
                    Offer(spare, frame);
                }

                Volatile.Write(ref _writing, Environment.TickCount64);
                Send(stream, frame);
                Volatile.Write(ref _writing, 0);
                Watch();
            }
            catch (Exception) when (_disposed)
            {
                return;
            }
            catch (Exception ex)
            {
                // Whatever ends one websocket, the carrier holds its port and opens another one on the next
                // datagram; only a carrier taken down stops the pump.
                Volatile.Write(ref _writing, 0);
                if (stream is null)
                {
                    _note?.Invoke($"the carrier's own port {LocalPort} refused a datagram", ex);
                }

                Drop(stream, ex);
                if (stream is null || Volatile.Read(ref _stream) is null)
                {
                    Pause();
                }
            }
        }
    }

    // Opens another websocket beside the one in use where that one was answering steadily and has brought nothing
    // since, while the tunnel keeps sending.
    private void Watch()
    {
        if (Interlocked.Increment(ref _unanswered) < QuietSent)
        {
            return;
        }

        var now = Environment.TickCount64;
        var answered = Volatile.Read(ref _answered);
        if (now - answered < QuietMs
            || answered - Volatile.Read(ref _steadySince) < SteadyMs
            || now - Volatile.Read(ref _watched) < QuietHoldMs
            || !Renew())
        {
            return;
        }

        Volatile.Write(ref _watched, now);
        _note?.Invoke($"the server was answering through the websocket to {_front.Host}:{_front.Port} and has brought nothing for {now - answered} ms while the tunnel kept sending, so another one is opened beside it", null);
    }

    // Copies what leaves on the websocket in use to the one on trial, so the server can answer through it.
    private void Offer(Stream spare, ReadOnlySpan<byte> frame)
    {
        try
        {
            Volatile.Write(ref _offering, Environment.TickCount64);
            Send(spare, frame);
            Volatile.Write(ref _offering, 0);
        }
        catch (Exception ex) when (!_disposed)
        {
            Volatile.Write(ref _offering, 0);
            Drop(spare, ex);
        }
    }

    // Frames what the loopback holds into one buffer: the first datagram is waited for and the rest are taken while
    // they still fit, so a burst leaves as one write instead of one write a packet.
    private int Fill()
    {
        var received = _local.ReceiveFrom(_outgoing.AsSpan(FrameOverhead), SocketFlags.None, _from);
        Remember();
        var used = Frame(_outgoing, 0, received);
        for (var waiting = Waiting(); waiting > 0 && used + FrameOverhead + waiting <= _outgoing.Length; waiting = Waiting())
        {
            var more = _local.ReceiveFrom(_outgoing.AsSpan(used + FrameOverhead), SocketFlags.None, _from);
            Remember();
            used = Frame(_outgoing, used, more);
        }

        return used;
    }

    // Bytes of the next datagram already on the loopback, none where nothing waits.
    private int Waiting()
    {
        try
        {
            return _local.Available;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Puts a header in front of a payload already lying a frame overhead past the offset and says where the buffer
    /// now ends. A payload short enough for the two-byte header moves back the two bytes it saves, so the frames
    /// stay one after another and the whole run leaves as one write.
    /// </summary>
    internal static int Frame(Span<byte> buffer, int offset, int length)
    {
        var header = length < 126 ? 2 : 4;
        if (header == 2)
        {
            buffer.Slice(offset + FrameOverhead, length).CopyTo(buffer[(offset + header)..]);
            buffer[offset + 1] = (byte)length;
        }
        else
        {
            buffer[offset + 1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(buffer[(offset + 2)..], (ushort)length);
        }

        buffer[offset] = (byte)(FinalBit | OpBinary);
        return offset + header + length;
    }

    // Where answers go back to. The engine keeps one socket for a session, so this settles on the first datagram and
    // is copied again only where the engine rebinds.
    private void Remember()
    {
        if (_engine is not null && _engine.Equals(_from))
        {
            return;
        }

        var copy = new SocketAddress(_from.Family, _from.Size);
        _from.Buffer.Span[.._from.Size].CopyTo(copy.Buffer.Span);
        _engine = copy;
    }

    // One frame on the wire. Datagrams and the answers to the front's pings come from two threads, and the stream
    // carries one write at a time.
    private void Send(Stream stream, ReadOnlySpan<byte> frame)
    {
        lock (_sending)
        {
            stream.Write(frame);
        }
    }

    // Waits out the retry gap, or less where the carrier is taken down meanwhile.
    private void Pause()
    {
        try
        {
            _cts.Token.WaitHandle.WaitOne(RetryGapMs);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // The live websocket, opened on demand and no more often than the retry gap.
    private Stream? Ready()
    {
        if (Volatile.Read(ref _stream) is { } live)
        {
            return live;
        }

        var now = Environment.TickCount64;
        if (now - _attempted < RetryGapMs)
        {
            return null;
        }

        _attempted = now;
        var opened = Open(false);
        if (opened is null)
        {
            return null;
        }

        // A websocket opened beside the one that ended has taken its place meanwhile.
        var standing = Install(opened);
        if (!ReferenceEquals(standing, opened))
        {
            opened.Dispose();
            return standing;
        }

        Run(() => Deliver(opened), "ws carrier in");
        return opened;
    }

    // Puts a websocket just opened in use where none is, and says which one is in use after that.
    private Stream? Install(Stream opened)
    {
        lock (_gate)
        {
            if (!_disposed && _stream is null)
            {
                _stream = opened;
                Fresh();
            }

            return _stream;
        }
    }

    /// <summary>
    /// Opens another websocket beside the one in use and moves the tunnel to it once the server answers through
    /// it, so a connection that went silent costs the tunnel no gap. False where no websocket is in use, or
    /// another one is being tried already.
    /// </summary>
    public bool Renew()
    {
        if (_disposed || Volatile.Read(ref _stream) is not { } inUse || Interlocked.CompareExchange(ref _renewing, 1, 0) != 0)
        {
            return false;
        }

        Run(() => Beside(inUse), "ws carrier beside");
        return true;
    }

    // Dials a websocket beside the one in use and tries it; after that another one may be asked for.
    private void Beside(Stream inUse)
    {
        try
        {
            var opened = Open(true);
            if (opened is null)
            {
                Volatile.Write(ref _watched, Environment.TickCount64 - QuietHoldMs + QuietRetryMs);
                _note?.Invoke("no other websocket could be opened, so the tunnel stays on the one in use", null);
                return;
            }

            var since = Environment.TickCount64;
            if (Stand(opened, inUse))
            {
                Try(opened, since);
            }
        }
        catch (Exception) when (_disposed)
        {
            return;
        }
        catch (Exception ex)
        {
            _note?.Invoke($"trying another websocket to {_front.Host}:{_front.Port} failed", ex);
        }
        finally
        {
            Volatile.Write(ref _renewing, 0);
        }
    }

    // Puts a websocket just opened beside the one in use and says whether it is on trial. Where the one in use
    // ended meanwhile, the new one takes its place; where another has taken it already, the new one is closed.
    private bool Stand(Stream opened, Stream inUse)
    {
        var (trial, taken) = Place(opened, inUse);
        if (!trial && !taken)
        {
            opened.Dispose();
            return false;
        }

        var front = $"{_front.Host}:{_front.Port}";
        _note?.Invoke(trial
            ? $"another websocket to {front} stands beside the one in use, and both carry what the tunnel sends"
            : $"the websocket in use ended meanwhile, so the tunnel is on the one just opened to {front}", null);
        Run(() => Deliver(opened), "ws carrier in");
        return trial;
    }

    // Finds the place of a websocket just opened: on trial beside the one in use, or in use where none is left.
    private (bool Trial, bool Taken) Place(Stream opened, Stream inUse)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return (false, false);
            }

            if (ReferenceEquals(_stream, inUse))
            {
                _spare = opened;
                return (true, false);
            }

            if (_stream is not null)
            {
                return (false, false);
            }

            _stream = opened;
            Fresh();
            return (false, true);
        }
    }

    // Starts the clocks of a websocket that has just taken the tunnel.
    private void Fresh()
    {
        var now = Environment.TickCount64;
        Volatile.Write(ref _heard, now);
        Volatile.Write(ref _answered, now);
        Volatile.Write(ref _steadySince, now);
        Volatile.Write(ref _unanswered, 0);
    }

    // Waits for the websocket on trial to prove itself and settles which of the two the tunnel is left on.
    private void Try(Stream opened, long since)
    {
        while (Wait(TrialStepMs))
        {
            if (ReferenceEquals(Volatile.Read(ref _stream), opened))
            {
                Retire();
                return;
            }

            if (!ReferenceEquals(Volatile.Read(ref _spare), opened))
            {
                return;
            }

            var now = Environment.TickCount64;
            var offering = Volatile.Read(ref _offering);
            if (offering > 0 && now - offering >= BlockedMs)
            {
                if (Dismiss(opened))
                {
                    _note?.Invoke($"the websocket opened beside the one in use has taken no data for {now - offering} ms, so it is closed", null);
                }

                return;
            }

            var writing = Volatile.Read(ref _writing);
            if (writing > 0 && now - writing >= BlockedMs)
            {
                Promote(opened, $"the websocket in use has taken no data for {now - writing} ms, so the tunnel moves to the one opened beside it", false);
                return;
            }

            if (now - since < TrialMs)
            {
                continue;
            }

            if (Volatile.Read(ref _answered) < since)
            {
                Promote(opened, $"neither websocket brought an answer of the server in {TrialMs / 1000} s, so the tunnel moves to the fresh one", false);
                return;
            }

            if (Dismiss(opened))
            {
                _note?.Invoke($"the websocket in use still carries and the one opened beside it brought nothing in {TrialMs / 1000} s, so it is closed", null);
            }

            return;
        }
    }

    // Moves the tunnel to the websocket on trial. The one it leaves is closed at once, or kept to be heard out.
    private bool Promote(Stream spare, string said, bool linger)
    {
        var (moved, left) = Swap(spare, linger);
        if (!moved)
        {
            return false;
        }

        if (!linger)
        {
            left?.Dispose();
        }

        _note?.Invoke(said, null);
        return true;
    }

    // Puts the websocket on trial in use where it is still on trial, and hands back the one it replaced.
    private (bool Moved, Stream? Left) Swap(Stream spare, bool linger)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_spare, spare))
            {
                return (false, null);
            }

            var left = _stream;
            _stream = spare;
            _spare = null;
            if (linger)
            {
                _retiring = left;
            }

            Fresh();
            return (true, left);
        }
    }

    // Closes the websocket on trial where it is still that.
    private bool Dismiss(Stream spare)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_spare, spare))
            {
                return false;
            }

            _spare = null;
        }

        spare.Dispose();
        return true;
    }

    // Lets what was on its way through the websocket the tunnel left arrive, then closes it.
    private void Retire()
    {
        if (Interlocked.Exchange(ref _retiring, null) is not { } left)
        {
            return;
        }

        Wait(RetireMs);
        left.Dispose();
    }

    // Waits the time given; false where the carrier is taken down meanwhile.
    private bool Wait(int ms)
    {
        try
        {
            return !_cts.Token.WaitHandle.WaitOne(ms) && !_disposed;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    // Notes that the front spoke on the websocket in use.
    private void Heard(Stream stream)
    {
        if (ReferenceEquals(Volatile.Read(ref _stream), stream))
        {
            Volatile.Write(ref _heard, Environment.TickCount64);
        }
    }

    // Notes a datagram of the server: one that came through the websocket on trial moves the tunnel to it.
    private void Answered(Stream stream, long opened)
    {
        if (ReferenceEquals(Volatile.Read(ref _spare), stream))
        {
            Promote(stream, $"the server answered through the websocket opened beside the one in use {Environment.TickCount64 - opened} ms after it stood, so the tunnel moves to it", true);
            return;
        }

        if (!ReferenceEquals(Volatile.Read(ref _stream), stream))
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - Volatile.Read(ref _answered) > SteadyGapMs)
        {
            Volatile.Write(ref _steadySince, now);
        }

        Volatile.Write(ref _answered, now);
        Volatile.Write(ref _unanswered, 0);
    }

    /// <summary>
    /// Asks a front the same question the carrier asks on its first datagram and drops the answer. Nothing is
    /// carried, so an address can be checked before a tunnel is built on it.
    /// </summary>
    public static async Task<(WsFrontOutcome Outcome, string Detail)> ProbeAsync(
        WsEndpoint front, IPAddress address, int targetPort, Func<string>? token, Func<Socket, bool>? bypass, CancellationToken ct)
    {
        var dial = await Task.Run(() => Dial(front, address, targetPort, token, bypass, ct), CancellationToken.None).ConfigureAwait(false);
        dial.Stream?.Dispose();
        return (dial.Outcome, dial.Detail);
    }

    // One websocket to the front: a connect to the resolved address, TLS, the upgrade. The socket is never asked
    // for anything asynchronous, so a thread waiting on it is woken by the system itself.
    private static (Stream? Stream, WsFrontOutcome Outcome, string Detail, Exception? Error) Dial(
        WsEndpoint front, IPAddress address, int targetPort, Func<string>? token, Func<Socket, bool>? bypass, CancellationToken ct)
    {
        var authorization = token?.Invoke();
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ConnectTimeoutMs);
        using var late = deadline.Token.Register(socket.Dispose);
        try
        {
            bypass?.Invoke(socket);
            socket.Connect(new IPEndPoint(address, front.Port));
            var tls = new SslStream(new NetworkStream(socket, ownsSocket: true));
            var options = new SslClientAuthenticationOptions { TargetHost = front.Host };
            if (authorization is not null)
            {
                options.RemoteCertificateValidationCallback = Presented;
            }

            tls.AuthenticateAsClient(options);
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            tls.Write(Encoding.ASCII.GetBytes(Handshake(front, targetPort, key, authorization)));
            var answer = Header(tls);
            if (!Accepted(answer, key))
            {
                tls.Dispose();
                return (null, WsFrontOutcome.Refused, FirstLine(answer), null);
            }

            socket.SendTimeout = WriteTimeoutMs;
            return (tls, WsFrontOutcome.Ok, string.Empty, null);
        }
        catch (AuthenticationException ex) when (!deadline.IsCancellationRequested)
        {
            socket.Dispose();
            return (null, WsFrontOutcome.Tls, ex.Message, ex);
        }
        catch (Exception ex) when (deadline.IsCancellationRequested || ex is SocketException or IOException or ObjectDisposedException)
        {
            socket.Dispose();
            return (null, WsFrontOutcome.NoAnswer, string.Empty, deadline.IsCancellationRequested ? null : ex);
        }
    }

    // Takes any certificate a front presents to a carrier that proves its keys by the token.
    private static bool Presented(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null;

    // The carrier's own dial, with the outcome written to the log the way the tunnel reads it.
    private Stream? Open(bool beside)
    {
        var dial = Dial(_front, _address, _targetPort, _token, _bypass, _cts.Token);
        var front = $"{_front.Host}:{_front.Port}";
        switch (dial.Outcome)
        {
            case WsFrontOutcome.Ok:
                if (!beside)
                {
                    _note?.Invoke($"the tunnel is carried inside a websocket to {front} and handed to port {_targetPort} on the server", null);
                }

                return dial.Stream;
            case WsFrontOutcome.Refused:
                _note?.Invoke($"the websocket front at {front} refused to carry the tunnel: {dial.Detail}", null);
                return null;
            case WsFrontOutcome.Tls:
                _note?.Invoke($"the websocket front at {front} did not come up under TLS", dial.Error);
                return null;
            default:
                _note?.Invoke(dial.Error is null
                    ? $"the websocket front at {front} did not answer in time"
                    : $"the websocket front at {front} could not be opened", dial.Error);
                return null;
        }
    }

    // Messages from the front, each one a datagram back to the engine. One read fills the buffer and every frame
    // it holds is taken from there, so a datagram no longer costs a pair of reads through tls.
    private void Deliver(Stream stream)
    {
        var frames = new Frames(stream);
        var control = new byte[ControlBytes + FrameOverhead];
        var mask = new byte[4];
        var message = new List<byte>();
        var opened = Environment.TickCount64;
        try
        {
            while (!_disposed)
            {
                var head = frames.Take(2);
                Heard(stream);
                var final = (head[0] & FinalBit) != 0;
                var opcode = (byte)(head[0] & 0x0f);
                var masked = (head[1] & MaskBit) != 0;
                var length = head[1] & 0x7f;
                if (length == 126)
                {
                    length = BinaryPrimitives.ReadUInt16BigEndian(frames.Take(2));
                }
                else if (length == 127)
                {
                    var counted = BinaryPrimitives.ReadUInt64BigEndian(frames.Take(8));
                    if (counted > MaxDatagram)
                    {
                        // Reading part of a frame leaves the rest of it to be read as the next one.
                        throw new IOException($"the websocket front sent a frame of {counted} bytes");
                    }

                    length = (int)counted;
                }

                if (masked)
                {
                    frames.Take(4).CopyTo(mask);
                }

                var payload = frames.Take(length);
                if (masked)
                {
                    for (var index = 0; index < length; index++)
                    {
                        payload[index] ^= mask[index & 3];
                    }
                }

                if (opcode == OpClose)
                {
                    Drop(stream, null);
                    return;
                }

                if (opcode == OpPing)
                {
                    if (length <= ControlBytes)
                    {
                        var pong = Encode(control, payload, OpPong);
                        Send(stream, control.AsSpan(0, pong));
                    }

                    continue;
                }

                if (opcode == OpPong)
                {
                    continue;
                }

                // A front that splits one datagram over several frames is rare, but a half datagram is not a packet.
                if (!final || message.Count > 0)
                {
                    message.AddRange(payload);
                    if (!final)
                    {
                        continue;
                    }
                }

                Answered(stream, opened);
                if (_engine is { } engine)
                {
                    _local.SendTo(message.Count > 0 ? message.ToArray() : payload, SocketFlags.None, engine);
                }

                message.Clear();
            }
        }
        catch (Exception ex)
        {
            Drop(stream, _disposed ? null : ex);
        }
    }

    /// <summary>
    /// Frames as they arrive from the front. One read fills the buffer and every frame it holds is taken from
    /// there; the buffer holds the longest frame the carrier accepts, so what is asked for always fits.
    /// </summary>
    private sealed class Frames
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[MaxDatagram + MaxFrameHeader];
        private int _start;
        private int _end;

        /// <summary>
        /// ctor
        /// </summary>
        public Frames(Stream stream)
        {
            _stream = stream;
        }

        /// <summary>
        /// The next bytes of the stream, held until the call after this one.
        /// </summary>
        public Span<byte> Take(int count)
        {
            while (_end - _start < count)
            {
                if (_start > 0 && _buffer.Length - _end < count)
                {
                    _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
                    _end -= _start;
                    _start = 0;
                }

                var read = _stream.Read(_buffer.AsSpan(_end));
                if (read <= 0)
                {
                    throw new IOException("the websocket front closed the connection");
                }

                _end += read;
            }

            var taken = _buffer.AsSpan(_start, count);
            _start += count;
            if (_start == _end)
            {
                _start = 0;
                _end = 0;
            }

            return taken;
        }
    }

    // The upgrade answer, read a byte at a time so the frames behind it stay in the stream.
    private static string Header(Stream stream)
    {
        var answer = new List<byte>();
        Span<byte> one = stackalloc byte[1];
        while (answer.Count < MaxHeaderBytes)
        {
            if (stream.Read(one) <= 0)
            {
                throw new IOException("the websocket front closed the connection");
            }

            answer.Add(one[0]);
            if (answer.Count >= 4 && answer[^4] == '\r' && answer[^3] == '\n' && answer[^2] == '\r' && answer[^1] == '\n')
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(answer.ToArray());
    }

    private static bool Accepted(string answer, string key)
    {
        if (!answer.StartsWith("HTTP/1.1 101", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The answer names the key back, hashed the one way the protocol prescribes.
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptSalt)));
        return answer.Contains(accept, StringComparison.Ordinal);
    }

    private static string FirstLine(string answer)
    {
        var line = answer.IndexOf('\r');
        return line > 0 ? answer[..line] : answer.Trim();
    }

    // Ends one websocket. The one in use hands the tunnel to the one on trial, else the next datagram opens another.
    // A stream already replaced is only closed, or a loop ending late would take down the connection that replaced
    // it.
    private void Drop(Stream? ended, Exception? ex)
    {
        if (ended is null)
        {
            return;
        }

        var (inUse, onTrial, heir) = Unseat(ended);
        ended.Dispose();
        if (_disposed)
        {
            return;
        }

        var front = $"{_front.Host}:{_front.Port}";
        if (inUse)
        {
            _note?.Invoke(heir is null
                ? $"the websocket to {front} ended; the tunnel opens another one on its next packet"
                : $"the websocket to {front} ended; the tunnel moves to the one opened beside it", ex);
        }
        else if (onTrial)
        {
            _note?.Invoke($"the websocket opened beside the one in use to {front} ended", ex);
        }
    }

    // Takes an ended websocket out of the place it stood in and says which place that was.
    private (bool InUse, bool OnTrial, Stream? Heir) Unseat(Stream ended)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_stream, ended))
            {
                var heir = _spare;
                _stream = heir;
                _spare = null;
                if (heir is not null)
                {
                    Fresh();
                }

                return (true, false, heir);
            }

            if (ReferenceEquals(_spare, ended))
            {
                _spare = null;
                return (false, true, null);
            }

            return (false, false, null);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        foreach (var stream in Release())
        {
            stream?.Dispose();
        }

        _local.Dispose();
        _cts.Dispose();
    }

    // Takes every websocket the carrier holds out of its hands.
    private Stream?[] Release()
    {
        lock (_gate)
        {
            var held = new[] { _stream, _spare, _retiring };
            _stream = null;
            _spare = null;
            _retiring = null;
            return held;
        }
    }
}
