using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AmneziaGeo.Routing;

/// <summary>
/// Proxy the application offers on a fixed port. Both ports take either protocol - the first byte a client sends
/// tells SOCKS5 from HTTP - and every destination is opened by the outbound the platform supplies, so what leaves
/// through the tunnel is decided in one place for the whole application. Only loopback reaches it until the local
/// network is allowed, and then only private addresses, with the credentials the settings name.
/// </summary>
public sealed class LocalProxyServer : IDisposable
{
    private const int HeadLimit = 8192;
    private const int BufferSize = 16384;
    private const int Backlog = 128;
    private const byte Version5 = 0x05;
    private const byte Version4 = 0x04;
    private const byte NoAuth = 0x00;
    private const byte UserPass = 0x02;
    private const byte NoMethod = 0xFF;
    private const byte CommandConnect = 0x01;
    private const byte AddressIpV4 = 0x01;
    private const byte AddressName = 0x03;
    private const byte AddressIpV6 = 0x04;
    private const byte ReplyOk = 0x00;
    private const byte ReplyFailure = 0x01;
    private const byte ReplyDenied = 0x02;
    private const byte ReplyNoCommand = 0x07;

    private readonly IProxyOutbound _outbound;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<Socket, byte> _open = new();
    private readonly object _sync = new();
    private readonly List<Socket> _listeners = [];
    private CancellationTokenSource? _cts;
    private LocalProxyOptions _options = new();
    private string _credentials = string.Empty;
    private long _accepted;
    private long _served;
    private long _refused;
    private long _blocked;
    private long _bytes;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public LocalProxyServer(IProxyOutbound outbound, Action<string> log)
    {
        _outbound = outbound;
        _log = log;
    }

    /// <summary>
    /// Whether the listener is up.
    /// </summary>
    public bool Running { get; private set; }

    /// <summary>
    /// Why the last start failed; empty while it holds.
    /// </summary>
    public string Error { get; private set; } = string.Empty;

    /// <summary>
    /// Payload carried in both directions so far.
    /// </summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>
    /// Takes the settings: starts, restarts or stops the listener to match them.
    /// </summary>
    public bool Apply(LocalProxyOptions options)
    {
        lock (_sync)
        {
            Halt();
            _options = options;
            _credentials = options.RequiresAuth
                ? Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.User}:{options.Password}"))
                : string.Empty;
            if (!options.Enabled || _disposed)
            {
                Error = string.Empty;
                return true;
            }

            if (!options.IsValid())
            {
                Error = "port out of range";
                return false;
            }

            var address = options.AllowLan ? IPAddress.Any : IPAddress.Loopback;
            var cts = new CancellationTokenSource();
            foreach (var port in options.Ports)
            {
                if (!Bind(address, port, cts.Token))
                {
                    Halt();
                    cts.Dispose();
                    return false;
                }
            }

            _cts = cts;
            Running = true;
            Error = string.Empty;
            _log($"proxy: listening on {address}:{string.Join(", :", options.Ports)}"
                + (options.RequiresAuth ? " with a password" : " without a password"));
            return true;
        }
    }

    /// <summary>
    /// Takes the listener down and drops what it holds.
    /// </summary>
    public void Stop()
    {
        lock (_sync)
        {
            Halt();
        }
    }

    /// <summary>
    /// Address of this machine on the local network, empty when it has none; the adapters named are passed over,
    /// so the tunnel's own address is not taken for it.
    /// </summary>
    public static string LanAddress(params string[] skip)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || skip.Any(name => adapter.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                    || adapter.Description.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(address.Address))
                {
                    return address.Address.ToString();
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// One line of what the proxy has carried.
    /// </summary>
    public string Snapshot()
    {
        return $"proxy: {Interlocked.Read(ref _served)}/{Interlocked.Read(ref _accepted)} served, "
            + $"{Interlocked.Read(ref _blocked)} blocked, {Interlocked.Read(ref _refused)} refused, {Bytes / 1024} KiB";
    }

    private bool Bind(IPAddress address, int port, CancellationToken ct)
    {
        try
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(address, port));
            listener.Listen(Backlog);
            _listeners.Add(listener);
            _ = Task.Run(() => AcceptAsync(listener, ct), CancellationToken.None);
            return true;
        }
        catch (SocketException ex)
        {
            Error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"port {port} is taken"
                : $"port {port}: {ex.SocketErrorCode}";
            _log($"proxy: {Error}");
            return false;
        }
    }

    // Stops the listeners and resets the live connections; the caller holds the lock.
    private void Halt()
    {
        Running = false;
        _cts?.Cancel();
        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }

        _listeners.Clear();
        _cts?.Dispose();
        _cts = null;
        Drop();
    }

    private async Task AcceptAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptAsync(ct).ConfigureAwait(false);
                _open.TryAdd(client, 0);
                Interlocked.Increment(ref _accepted);
                _ = Task.Run(() => ServeAsync(client, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
        }
    }

    // Takes one client: names the protocol by its first byte and hands what it asks for to the outbound.
    private async Task ServeAsync(Socket client, CancellationToken ct)
    {
        try
        {
            client.NoDelay = true;
            if (!Allowed(client.RemoteEndPoint as IPEndPoint))
            {
                Interlocked.Increment(ref _refused);
                return;
            }

            var first = await ReadByteAsync(client, ct).ConfigureAwait(false);
            if (first < 0)
            {
                Interlocked.Increment(ref _refused);
                return;
            }

            if (first == Version5)
            {
                await ServeSocksAsync(client, ct).ConfigureAwait(false);
                return;
            }

            if (first == Version4)
            {
                // SOCKS4 carries no name and no password; a client that speaks it also speaks HTTP.
                Interlocked.Increment(ref _refused);
                return;
            }

            await ServeHttpAsync(client, (byte)first, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _log("proxy: session failed: " + ex.Message);
        }
        finally
        {
            Close(client);
        }
    }

    // RFC 1928 with the user/password of RFC 1929; only CONNECT is served, so a client asking for UDP is told so
    // instead of being left waiting.
    private async Task ServeSocksAsync(Socket client, CancellationToken ct)
    {
        var count = await ReadByteAsync(client, ct).ConfigureAwait(false);
        if (count <= 0)
        {
            return;
        }

        var methods = new byte[count];
        if (!await ReadExactAsync(client, methods, ct).ConfigureAwait(false))
        {
            return;
        }

        var wanted = _options.RequiresAuth ? UserPass : NoAuth;
        if (Array.IndexOf(methods, wanted) < 0)
        {
            await SendAsync(client, [Version5, NoMethod], ct).ConfigureAwait(false);
            Interlocked.Increment(ref _refused);
            return;
        }

        await SendAsync(client, [Version5, wanted], ct).ConfigureAwait(false);
        if (wanted == UserPass && !await AuthenticateAsync(client, ct).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _refused);
            return;
        }

        var head = new byte[4];
        if (!await ReadExactAsync(client, head, ct).ConfigureAwait(false) || head[0] != Version5)
        {
            return;
        }

        if (head[1] != CommandConnect)
        {
            await SendAsync(client, Reply(ReplyNoCommand), ct).ConfigureAwait(false);
            Interlocked.Increment(ref _refused);
            return;
        }

        var host = await ReadAddressAsync(client, head[3], ct).ConfigureAwait(false);
        var port = await ReadPortAsync(client, ct).ConfigureAwait(false);
        if (host is null || port <= 0)
        {
            await SendAsync(client, Reply(ReplyFailure), ct).ConfigureAwait(false);
            return;
        }

        var (link, outcome) = await _outbound.ConnectAsync(host, port, ct).ConfigureAwait(false);
        if (link is null)
        {
            Count(outcome);
            await SendAsync(client, Reply(outcome == ProxyOutcome.Blocked ? ReplyDenied : ReplyFailure), ct)
                .ConfigureAwait(false);
            return;
        }

        using (link)
        {
            _open.TryAdd(link.Socket, 0);
            Interlocked.Increment(ref _served);
            await SendAsync(client, Reply(ReplyOk), ct).ConfigureAwait(false);
            await ExchangeAsync(client, link, ct).ConfigureAwait(false);
            _open.TryRemove(link.Socket, out _);
        }
    }

    // RFC 1929: one user and one password, compared whole.
    private async Task<bool> AuthenticateAsync(Socket client, CancellationToken ct)
    {
        var version = await ReadByteAsync(client, ct).ConfigureAwait(false);
        var user = await ReadStringAsync(client, ct).ConfigureAwait(false);
        var password = await ReadStringAsync(client, ct).ConfigureAwait(false);
        var ok = version == 0x01
            && string.Equals(user, _options.User, StringComparison.Ordinal)
            && string.Equals(password, _options.Password, StringComparison.Ordinal);
        await SendAsync(client, [0x01, ok ? ReplyOk : ReplyFailure], ct).ConfigureAwait(false);
        return ok;
    }

    // The head names the destination: CONNECT carries an authority, everything else an absolute URL that is
    // rewritten to the origin form the destination expects.
    private async Task ServeHttpAsync(Socket client, byte first, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(HeadLimit);
        try
        {
            buffer[0] = first;
            var used = await ReadHeadAsync(client, buffer, ct).ConfigureAwait(false);
            var end = HeadEnd(buffer, used);
            if (end <= 0)
            {
                Interlocked.Increment(ref _refused);
                return;
            }

            // What arrived with the head is the start of the body and goes on with it.
            var tail = buffer[end..used];
            var text = Encoding.ASCII.GetString(buffer, 0, end);
            if (_options.RequiresAuth && !Authorized(text))
            {
                await SendTextAsync(client, "HTTP/1.1 407 Proxy Authentication Required\r\n"
                    + "Proxy-Authenticate: Basic realm=\"AmneziaGeo\"\r\nConnection: close\r\n\r\n", ct).ConfigureAwait(false);
                Interlocked.Increment(ref _refused);
                return;
            }

            var request = ParseHttp(text);
            if (request is null)
            {
                await SendTextAsync(client, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n", ct).ConfigureAwait(false);
                Interlocked.Increment(ref _refused);
                return;
            }

            var (link, outcome) = await _outbound.ConnectAsync(request.Host, request.Port, ct).ConfigureAwait(false);
            if (link is null)
            {
                Count(outcome);
                await SendTextAsync(client, outcome == ProxyOutcome.Blocked
                    ? "HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n"
                    : "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n", ct).ConfigureAwait(false);
                return;
            }

            using (link)
            {
                _open.TryAdd(link.Socket, 0);
                Interlocked.Increment(ref _served);
                if (request.Connect)
                {
                    await SendTextAsync(client, "HTTP/1.1 200 Connection established\r\n\r\n", ct).ConfigureAwait(false);
                }
                else
                {
                    var head = Encoding.ASCII.GetBytes(request.Head);
                    await link.Socket.SendAsync(head, SocketFlags.None, ct).ConfigureAwait(false);
                    Count(link, head.Length);
                }

                if (tail.Length > 0)
                {
                    await link.Socket.SendAsync(tail, SocketFlags.None, ct).ConfigureAwait(false);
                    Count(link, tail.Length);
                }

                await ExchangeAsync(client, link, ct).ConfigureAwait(false);
                _open.TryRemove(link.Socket, out _);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Passes the bytes both ways until either side is done.
    private async Task ExchangeAsync(Socket client, IProxyLink link, CancellationToken ct)
    {
        await Task.WhenAll(
            PumpAsync(client, link.Socket, link, ct),
            PumpAsync(link.Socket, client, link, ct)).ConfigureAwait(false);
    }

    // Copies one direction until end of stream and half-closes the far side.
    private async Task PumpAsync(Socket from, Socket to, IProxyLink link, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await from.ReceiveAsync(buffer.AsMemory(0, BufferSize), SocketFlags.None, ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, ct).ConfigureAwait(false);
                Count(link, read);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        try
        {
            to.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool Allowed(IPEndPoint? peer)
    {
        if (peer is null)
        {
            return false;
        }

        var address = peer.Address.IsIPv4MappedToIPv6 ? peer.Address.MapToIPv4() : peer.Address;
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        return _options.AllowLan && IsPrivate(address);
    }

    // Only the private ranges are let in: on a public network an open port is an open proxy.
    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xFE) == 0xFC;
    }

    private bool Authorized(string head)
    {
        foreach (var line in head.Split("\r\n"))
        {
            if (!line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["Proxy-Authorization:".Length..].Trim();
            return value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                && string.Equals(value["Basic ".Length..].Trim(), _credentials, StringComparison.Ordinal);
        }

        return false;
    }

    private void Count(IProxyLink link, int bytes)
    {
        Interlocked.Add(ref _bytes, bytes);
        link.Count(bytes);
    }

    private void Count(ProxyOutcome outcome)
    {
        if (outcome == ProxyOutcome.Blocked)
        {
            Interlocked.Increment(ref _blocked);
            return;
        }

        Interlocked.Increment(ref _refused);
    }

    private static byte[] Reply(byte code)
    {
        return [Version5, code, 0x00, AddressIpV4, 0, 0, 0, 0, 0, 0];
    }

    private static async Task<int> ReadByteAsync(Socket socket, CancellationToken ct)
    {
        var one = new byte[1];
        return await ReadExactAsync(socket, one, ct).ConfigureAwait(false) ? one[0] : -1;
    }

    private static async Task<bool> ReadExactAsync(Socket socket, Memory<byte> buffer, CancellationToken ct)
    {
        var used = 0;
        while (used < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer[used..], SocketFlags.None, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return false;
            }

            used += read;
        }

        return true;
    }

    // A length-prefixed string of the SOCKS5 handshake.
    private static async Task<string?> ReadStringAsync(Socket socket, CancellationToken ct)
    {
        var length = await ReadByteAsync(socket, ct).ConfigureAwait(false);
        if (length < 0)
        {
            return null;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        var raw = new byte[length];
        return await ReadExactAsync(socket, raw, ct).ConfigureAwait(false) ? Encoding.UTF8.GetString(raw) : null;
    }

    private static async Task<string?> ReadAddressAsync(Socket socket, byte kind, CancellationToken ct)
    {
        switch (kind)
        {
            case AddressIpV4:
            {
                var raw = new byte[4];
                return await ReadExactAsync(socket, raw, ct).ConfigureAwait(false) ? new IPAddress(raw).ToString() : null;
            }

            case AddressIpV6:
            {
                var raw = new byte[16];
                return await ReadExactAsync(socket, raw, ct).ConfigureAwait(false) ? new IPAddress(raw).ToString() : null;
            }

            case AddressName:
                return await ReadStringAsync(socket, ct).ConfigureAwait(false);

            default:
                return null;
        }
    }

    private static async Task<int> ReadPortAsync(Socket socket, CancellationToken ct)
    {
        var raw = new byte[2];
        return await ReadExactAsync(socket, raw, ct).ConfigureAwait(false) ? (raw[0] << 8) | raw[1] : 0;
    }

    // Reads the head, keeping the byte the protocol check already took.
    private static async Task<int> ReadHeadAsync(Socket client, byte[] buffer, CancellationToken ct)
    {
        var used = 1;
        while (used < HeadLimit)
        {
            if (HeadEnd(buffer, used) > 0)
            {
                return used;
            }

            var read = await client.ReceiveAsync(buffer.AsMemory(used, HeadLimit - used), SocketFlags.None, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return 0;
            }

            used += read;
        }

        return 0;
    }

    private static int HeadEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i] == (byte)'\n' && buffer[i - 1] == (byte)'\r' && buffer[i - 2] == (byte)'\n' && buffer[i - 3] == (byte)'\r')
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// What one proxied request asks for; the head is what goes to the destination.
    /// </summary>
    private sealed record HttpRequest(string Host, int Port, bool Connect, string Head);

    // Splits the request head: an authority for CONNECT, an absolute URL otherwise.
    private static HttpRequest? ParseHttp(string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 3)
        {
            return null;
        }

        if (string.Equals(parts[0], "CONNECT", StringComparison.Ordinal))
        {
            var (name, port) = SplitAuthority(parts[1], 443);
            return name is null ? null : new HttpRequest(name, port, true, string.Empty);
        }

        if (!parts[1].StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = parts[1]["http://".Length..];
        var slash = rest.IndexOf('/');
        var (host, tcpPort) = SplitAuthority(slash < 0 ? rest : rest[..slash], 80);
        if (host is null)
        {
            return null;
        }

        // The head goes on with the destination in origin form and without the headers of this hop; the response
        // ends the connection, so the next request is decided on its own.
        var text = new StringBuilder($"{parts[0]} {(slash < 0 ? "/" : rest[slash..])} {parts[2]}\r\n");
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length > 0 && !Hop(lines[i]))
            {
                text.Append(lines[i]).Append("\r\n");
            }
        }

        text.Append("Connection: close\r\n\r\n");
        return new HttpRequest(host, tcpPort, false, text.ToString());
    }

    private static bool Hop(string header)
    {
        return header.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)
            || header.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase)
            || header.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)
            || header.StartsWith("Keep-Alive:", StringComparison.OrdinalIgnoreCase);
    }

    private static (string? Host, int Port) SplitAuthority(string authority, int fallback)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            return (null, fallback);
        }

        var colon = authority.LastIndexOf(':');
        if (colon <= 0 || authority.IndexOf(']') > colon)
        {
            return (authority, fallback);
        }

        return int.TryParse(authority[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? (authority[..colon], port)
            : (authority, fallback);
    }

    private static async Task SendAsync(Socket socket, byte[] payload, CancellationToken ct)
    {
        await socket.SendAsync(payload, SocketFlags.None, ct).ConfigureAwait(false);
    }

    private static async Task SendTextAsync(Socket socket, string text, CancellationToken ct)
    {
        await socket.SendAsync(Encoding.ASCII.GetBytes(text), SocketFlags.None, ct).ConfigureAwait(false);
    }

    private void Close(Socket socket)
    {
        _open.TryRemove(socket, out _);
        socket.Dispose();
    }

    // Resets what is still open: a graceful close waits for an acknowledgement a torn tunnel no longer carries.
    private void Drop()
    {
        foreach (var pair in _open)
        {
            _open.TryRemove(pair.Key, out _);
            try
            {
                pair.Key.LingerState = new LingerOption(true, 0);
                pair.Key.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Halt();
        }
    }
}
