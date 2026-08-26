using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
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
    /// TLS did not come up under the front's own name.
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
/// costs nothing extra: the engine repeats an unanswered handshake on its own.
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

    // How long the front may say nothing at all before the websocket counts as gone: it pings on its own every
    // half minute, so silence this long is a connection that stands in name only.
    private const int SilenceMs = 75000;

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
    private readonly Func<Socket, bool>? _bypass;
    private readonly Action<string, Exception?>? _note;
    private readonly Socket _local;
    private readonly byte[] _outgoing = new byte[MaxDatagram + FrameOverhead];
    private readonly SocketAddress _from = new(AddressFamily.InterNetwork);
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private Stream? _stream;
    private SocketAddress? _engine;
    private long _attempted;
    private long _heard;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    private WsCarrier(WsEndpoint front, IPAddress address, int targetPort, Func<Socket, bool>? bypass, Action<string, Exception?>? note)
    {
        _front = front;
        _address = address;
        _targetPort = targetPort;
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
    /// Binds the loopback port and starts carrying datagrams. The front is dialled at an address resolved by the
    /// caller, because a lookup made after the tunnel is built would travel inside it and answer nothing.
    /// </summary>
    public static WsCarrier Start(WsEndpoint front, IPAddress address, int targetPort, Func<Socket, bool>? bypass, Action<string, Exception?>? note)
    {
        var carrier = new WsCarrier(front, address, targetPort, bypass, note);
        _ = Task.Run(() => carrier.PumpAsync(carrier._cts.Token));
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
    /// The upgrade request the front expects: what to open travels as a token in the protocol header.
    /// </summary>
    internal static string Handshake(WsEndpoint front, int targetPort, string key)
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
        if (front.Credentials.Length > 0)
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

    // Datagrams from the engine, each one a message on the front.
    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var stream = default(Stream);
            try
            {
                var used = await FillAsync(ct).ConfigureAwait(false);
                stream = await ReadyAsync(ct).ConfigureAwait(false);
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

                await SendAsync(stream, _outgoing.AsMemory(0, used), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                return;
            }
            catch (Exception ex)
            {
                // Whatever ends one websocket, the carrier holds its port and opens another one on the next
                // datagram; only a carrier taken down stops the pump.
                if (stream is null)
                {
                    _note?.Invoke($"the carrier's own port {LocalPort} refused a datagram", ex);
                }

                Drop(stream, ex);
                await PauseAsync(ct).ConfigureAwait(false);
            }
        }
    }

    // Frames what the loopback holds into one buffer: the first datagram is waited for and the rest are taken while
    // they still fit, so a burst leaves as one write instead of one write a packet.
    private async Task<int> FillAsync(CancellationToken ct)
    {
        var received = await _local.ReceiveFromAsync(_outgoing.AsMemory(FrameOverhead), SocketFlags.None, _from, ct)
            .ConfigureAwait(false);
        Remember();
        var used = Frame(_outgoing, 0, received);
        for (var waiting = Waiting(); waiting > 0 && used + FrameOverhead + waiting <= _outgoing.Length; waiting = Waiting())
        {
            var more = await _local.ReceiveFromAsync(_outgoing.AsMemory(used + FrameOverhead), SocketFlags.None, _from, ct)
                .ConfigureAwait(false);
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

    // One frame on the wire. Datagrams and the answers to the front's pings come from two loops, and the stream
    // carries one write at a time.
    private async Task SendAsync(Stream stream, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        await _sending.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }

    // Waits out the retry gap without turning a carrier taken down into a failure.
    private static async Task PauseAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(RetryGapMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // The live websocket, opened on demand and no more often than the retry gap.
    private async Task<Stream?> ReadyAsync(CancellationToken ct)
    {
        if (_stream is { } live)
        {
            return live;
        }

        var now = Environment.TickCount64;
        if (now - _attempted < RetryGapMs)
        {
            return null;
        }

        _attempted = now;
        var opened = await OpenAsync(ct).ConfigureAwait(false);
        if (opened is null)
        {
            return null;
        }

        Volatile.Write(ref _heard, Environment.TickCount64);
        _stream = opened;
        _ = Task.Run(() => DeliverAsync(opened, ct));
        return opened;
    }

    /// <summary>
    /// Asks a front the same question the carrier asks on its first datagram and drops the answer. Nothing is
    /// carried, so an address can be checked before a tunnel is built on it.
    /// </summary>
    public static async Task<(WsFrontOutcome Outcome, string Detail)> ProbeAsync(
        WsEndpoint front, IPAddress address, int targetPort, Func<Socket, bool>? bypass, CancellationToken ct)
    {
        var dial = await DialAsync(front, address, targetPort, bypass, ct).ConfigureAwait(false);
        if (dial.Stream is { } stream)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        return (dial.Outcome, dial.Detail);
    }

    // One websocket to the front: a connect to the resolved address, TLS under the front's own name, the upgrade.
    private static async Task<(Stream? Stream, WsFrontOutcome Outcome, string Detail, Exception? Error)> DialAsync(
        WsEndpoint front, IPAddress address, int targetPort, Func<Socket, bool>? bypass, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            bypass?.Invoke(socket);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ConnectTimeoutMs);
            await socket.ConnectAsync(new IPEndPoint(address, front.Port), deadline.Token).ConfigureAwait(false);
            var tls = new SslStream(new NetworkStream(socket, ownsSocket: true));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = front.Host }, deadline.Token).ConfigureAwait(false);
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            await tls.WriteAsync(Encoding.ASCII.GetBytes(Handshake(front, targetPort, key)), deadline.Token).ConfigureAwait(false);
            var answer = await HeaderAsync(tls, deadline.Token).ConfigureAwait(false);
            if (!Accepted(answer, key))
            {
                await tls.DisposeAsync().ConfigureAwait(false);
                return (null, WsFrontOutcome.Refused, FirstLine(answer), null);
            }

            return (tls, WsFrontOutcome.Ok, string.Empty, null);
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            return (null, WsFrontOutcome.NoAnswer, string.Empty, null);
        }
        catch (AuthenticationException ex)
        {
            socket.Dispose();
            return (null, WsFrontOutcome.Tls, ex.Message, ex);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            socket.Dispose();
            return (null, WsFrontOutcome.NoAnswer, string.Empty, ex);
        }
    }

    // The carrier's own dial, with the outcome written to the log the way the tunnel reads it.
    private async Task<Stream?> OpenAsync(CancellationToken ct)
    {
        var dial = await DialAsync(_front, _address, _targetPort, _bypass, ct).ConfigureAwait(false);
        var front = $"{_front.Host}:{_front.Port}";
        switch (dial.Outcome)
        {
            case WsFrontOutcome.Ok:
                _note?.Invoke($"the tunnel is carried inside a websocket to {front} and handed to port {_targetPort} on the server", null);
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
    private async Task DeliverAsync(Stream stream, CancellationToken ct)
    {
        var frames = new Frames(stream);
        var control = new byte[ControlBytes + FrameOverhead];
        var mask = new byte[4];
        var message = new List<byte>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var head = await frames.TakeAsync(2, ct).ConfigureAwait(false);
                Volatile.Write(ref _heard, Environment.TickCount64);
                var final = (head.Span[0] & FinalBit) != 0;
                var opcode = (byte)(head.Span[0] & 0x0f);
                var masked = (head.Span[1] & MaskBit) != 0;
                var length = head.Span[1] & 0x7f;
                if (length == 126)
                {
                    var wide = await frames.TakeAsync(2, ct).ConfigureAwait(false);
                    length = BinaryPrimitives.ReadUInt16BigEndian(wide.Span);
                }
                else if (length == 127)
                {
                    var wide = await frames.TakeAsync(8, ct).ConfigureAwait(false);
                    var counted = BinaryPrimitives.ReadUInt64BigEndian(wide.Span);
                    if (counted > MaxDatagram)
                    {
                        // Reading part of a frame leaves the rest of it to be read as the next one.
                        throw new IOException($"the websocket front sent a frame of {counted} bytes");
                    }

                    length = (int)counted;
                }

                if (masked)
                {
                    var key = await frames.TakeAsync(4, ct).ConfigureAwait(false);
                    key.Span.CopyTo(mask);
                }

                var payload = await frames.TakeAsync(length, ct).ConfigureAwait(false);
                if (masked)
                {
                    for (var index = 0; index < length; index++)
                    {
                        payload.Span[index] ^= mask[index & 3];
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
                        var pong = Encode(control, payload.Span, OpPong);
                        await SendAsync(stream, control.AsMemory(0, pong), ct).ConfigureAwait(false);
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
                    message.AddRange(payload.Span);
                    if (!final)
                    {
                        continue;
                    }
                }

                ReadOnlyMemory<byte> datagram = message.Count > 0 ? message.ToArray() : payload;
                message.Clear();
                if (_engine is { } engine)
                {
                    await _local.SendToAsync(datagram, SocketFlags.None, engine, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Drop(stream, ex);
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
        public async ValueTask<Memory<byte>> TakeAsync(int count, CancellationToken ct)
        {
            while (_end - _start < count)
            {
                if (_start > 0 && _buffer.Length - _end < count)
                {
                    _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
                    _end -= _start;
                    _start = 0;
                }

                var read = await _stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new IOException("the websocket front closed the connection");
                }

                _end += read;
            }

            var taken = _buffer.AsMemory(_start, count);
            _start += count;
            if (_start == _end)
            {
                _start = 0;
                _end = 0;
            }

            return taken;
        }
    }

    // Fills the whole buffer; a front that stops mid-frame has ended the connection.
    private static async Task ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[filled..], ct).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new IOException("the websocket front closed the connection");
            }

            filled += read;
        }
    }

    // The upgrade answer, read a byte at a time so the frames behind it stay in the stream.
    private static async Task<string> HeaderAsync(Stream stream, CancellationToken ct)
    {
        var answer = new List<byte>();
        var one = new byte[1];
        while (answer.Count < MaxHeaderBytes)
        {
            await ReadAsync(stream, one, ct).ConfigureAwait(false);
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

    // Ends one websocket; the next datagram opens another. A stream already replaced is left where it is, or a
    // loop ending late would take down the connection that replaced it.
    private void Drop(Stream? ended, Exception? ex)
    {
        if (ended is null || Interlocked.CompareExchange(ref _stream, null, ended) != ended)
        {
            ended?.Dispose();
            return;
        }

        ended.Dispose();
        if (!_disposed)
        {
            _note?.Invoke($"the websocket to {_front.Host}:{_front.Port} ended; the tunnel opens another one on its next packet", ex);
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
        Drop(Volatile.Read(ref _stream), null);
        _local.Dispose();
        _cts.Dispose();
        _sending.Dispose();
    }
}
