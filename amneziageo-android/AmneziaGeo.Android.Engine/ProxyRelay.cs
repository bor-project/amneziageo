using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Routing;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// Local HTTP proxy the tunnel offers to the applications. A request arrives as a name, so the destination is
/// decided while the session runs instead of at connect: a blocked name is refused, a direct one leaves on a
/// protected socket, and one no rule names follows the tunnel flag. A decision is held for an idle window, extended
/// by traffic and by every live connection, and dropped once nothing has used it - the next request decides again.
/// A plain request carries one destination, so its connection ends with the response and the next request is decided
/// on its own; a tunnel opened by CONNECT carries the one name it was opened for.
/// </summary>
internal sealed class ProxyRelay : IDisposable
{
    private const int HeadLimit = 8192;
    private const int BufferSize = 16384;
    private const int ConnectTimeoutMs = 8000;
    private const int MinSweepMs = 5_000;
    private const int MaxSweepMs = 30_000;
    private const int MaxEntries = 4096;
    private const int TopHosts = 6;
    private const int SessionRows = SessionReport.MaxRows;

    private readonly DomainMatcher _proxyNames;
    private readonly DomainMatcher _directNames;
    private readonly DomainMatcher _blockNames;
    private readonly GeoIpRanges _proxyRanges;
    private readonly GeoIpRanges _directRanges;
    private readonly GeoIpRanges _blockRanges;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _owners = new(StringComparer.Ordinal);
    private readonly HashSet<string> _apps;
    private readonly ConcurrentDictionary<Socket, byte> _open = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<int, bool> _protect;
    private readonly Action<string> _log;
    private readonly Func<IPEndPoint, string?>? _owner;
    private readonly RouteVerdict _undecided;
    private readonly string _rules;
    private readonly long _idleTtlMs;
    private Socket? _listener;
    private long _accepted;
    private long _served;
    private long _blocked;
    private long _refused;
    private long _released;
    private long _bytes;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public ProxyRelay(GeoRoutingPlan plan, Func<int, bool> protect, Action<string> log, Func<IPEndPoint, string?>? owner)
    {
        _proxyNames = new DomainMatcher(plan.ProxyDomains);
        _directNames = new DomainMatcher(plan.DirectDomains);
        _blockNames = new DomainMatcher(plan.BlockDomains);
        _proxyRanges = GeoIpRanges.Build(plan.ProxyRoutes);
        _directRanges = GeoIpRanges.Build(plan.DirectRoutes);
        _blockRanges = GeoIpRanges.Build(plan.BlockRoutes);
        _idleTtlMs = Math.Max(1, plan.TtlSeconds) * 1000L;
        _rules = $"{plan.ProxyRoutes.Count + plan.DirectRoutes.Count + plan.BlockRoutes.Count} range(s) and "
            + $"{plan.ProxyDomains.Count + plan.DirectDomains.Count + plan.BlockDomains.Count} name(s)";
        _undecided = plan.FullTunnel ? RouteVerdict.Proxy : RouteVerdict.Direct;
        _apps = new HashSet<string>(plan.TunnelApps, StringComparer.Ordinal);
        _protect = protect;
        _log = log;
        _owner = owner;
    }

    /// <summary>
    /// Port the proxy listens on; 0 until it binds.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Payload carried in both directions so far.
    /// </summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>
    /// Binds the loopback listener and starts serving; 0 when the port could not be taken.
    /// </summary>
    public int Start()
    {
        try
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(128);
            _listener = listener;
            Port = listener.LocalEndPoint is IPEndPoint bound ? bound.Port : 0;
            _ = Task.Run(() => AcceptAsync(listener, _cts.Token));
            _ = Task.Run(() => SweepAsync(_cts.Token));
            return Port;
        }
        catch (SocketException ex)
        {
            global::Android.Util.Log.Error("ProxyRelay", "listener did not bind: " + ex);
            return 0;
        }
    }

    /// <summary>
    /// One line of what the relay holds and has carried.
    /// </summary>
    public string Snapshot()
    {
        var top = new List<string>();
        foreach (var entry in _entries.OrderByDescending(pair => pair.Value.Bytes).Take(TopHosts))
        {
            top.Add($"{entry.Key} {Word(entry.Value.Verdict)} {entry.Value.Bytes / 1024}K");
        }

        return $"relay: {Interlocked.Read(ref _served)}/{Interlocked.Read(ref _accepted)} served, "
            + $"{Interlocked.Read(ref _blocked)} blocked, {Interlocked.Read(ref _refused)} refused, "
            + $"{Bytes / 1024} KiB, {_entries.Count} held of {_rules} in the rules, "
            + $"{Interlocked.Read(ref _released)} released"
            + $", apps [{string.Join(' ', _owners.Keys)}]; top: {string.Join(", ", top)}";
    }

    /// <summary>
    /// What the relay holds right now, busiest first. The head runs in another process, so this is rendered
    /// whole rather than queried, and each rate is what its destination carried since the previous snapshot.
    /// </summary>
    public SessionReport Sessions()
    {
        var now = Environment.TickCount64;
        var all = _entries.Values.ToList();
        var undecided = 0;
        foreach (var entry in all)
        {
            if (entry.Verdict == RouteVerdict.None)
            {
                undecided++;
            }
        }

        var rows = new List<LiveSession>();
        foreach (var entry in all.OrderByDescending(one => Volatile.Read(ref one.Bytes)).Take(SessionRows))
        {
            rows.Add(Row(entry, now));
        }

        return new SessionReport(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rows, all.Count, undecided, Bytes);
    }

    // One held destination as the head reads it.
    private static LiveSession Row(Entry entry, long now)
    {
        var bytes = Volatile.Read(ref entry.Bytes);
        var span = Math.Max(1, now - Volatile.Read(ref entry.ReportedAt));
        var bits = (bytes - Volatile.Read(ref entry.Reported)) * 8000 / span;
        Volatile.Write(ref entry.Reported, bytes);
        Volatile.Write(ref entry.ReportedAt, now);
        return new LiveSession(
            entry.Host,
            Word(entry.Verdict),
            bytes,
            bits,
            Volatile.Read(ref entry.Live),
            (int)((now - entry.Since) / 1000),
            (int)((now - Volatile.Read(ref entry.LastTouch)) / 1000),
            entry.App);
    }

    private static string Word(RouteVerdict verdict)
    {
        return verdict switch
        {
            RouteVerdict.Block => "block",
            RouteVerdict.Direct => "direct",
            RouteVerdict.Proxy => "proxy",
            _ => LiveSession.Undecided,
        };
    }

    // Turns an undecided destination into an action: an application the list names rides the tunnel, and the tunnel
    // flag says where what no rule named belongs for everyone else. A rule that decided wins over both, so a
    // destination the list sends direct stays direct even for a named application.
    private RouteVerdict Effective(RouteVerdict verdict, string app)
    {
        if (verdict != RouteVerdict.None)
        {
            return verdict;
        }

        return app.Length > 0 && _apps.Contains(app) ? RouteVerdict.Proxy : _undecided;
    }

    private async Task AcceptAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptAsync(ct).ConfigureAwait(false);
                Hold(client);
                Interlocked.Increment(ref _accepted);
                _ = Task.Run(() => ServeAsync(client, ct), ct);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                global::Android.Util.Log.Warn("ProxyRelay", "accept failed: " + ex);
            }
        }
    }

    // Reads the request, decides the destination, opens it the way the verdict says and passes the bytes both ways.
    private async Task ServeAsync(Socket client, CancellationToken ct)
    {
        var target = default(Socket);
        var entry = default(Entry);
        var held = false;
        try
        {
            client.NoDelay = true;
            var request = await ReadRequestAsync(client, ct).ConfigureAwait(false);
            if (request is null)
            {
                Interlocked.Increment(ref _refused);
                return;
            }

            entry = Touch(request.Host);
            var app = NoteOwner(client, entry);
            if (entry.Verdict == RouteVerdict.Block)
            {
                Interlocked.Increment(ref _blocked);
                await SendAsync(client, "HTTP/1.1 403 Forbidden\r\n\r\n", ct).ConfigureAwait(false);
                return;
            }

            Interlocked.Increment(ref entry.Live);
            held = true;
            target = await ConnectAsync(entry, request.Port, app, ct).ConfigureAwait(false);
            if (target is null)
            {
                Interlocked.Increment(ref _refused);
                await SendAsync(client, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n", ct).ConfigureAwait(false);
                return;
            }

            Interlocked.Increment(ref _served);
            if (request.Connect)
            {
                await SendAsync(client, "HTTP/1.1 200 Connection established\r\n\r\n", ct).ConfigureAwait(false);
                await Task.WhenAll(
                    PumpAsync(client, target, entry, ct),
                    PumpAsync(target, client, entry, ct)).ConfigureAwait(false);
                return;
            }

            await target.SendAsync(request.Head, SocketFlags.None, ct).ConfigureAwait(false);
            Count(entry, request.Head.Length);
            await ExchangeAsync(client, target, entry, ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException)
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
            global::Android.Util.Log.Warn("ProxyRelay", "relay failed: " + ex);
        }
        finally
        {
            if (held)
            {
                Interlocked.Decrement(ref entry!.Live);
                Volatile.Write(ref entry.LastTouch, Environment.TickCount64);
            }

            Close(target);
            Close(client);
        }
    }

    // Carries one exchange: the body up, the response down, and the upload stops with the response.
    private async Task ExchangeAsync(Socket client, Socket target, Entry entry, CancellationToken ct)
    {
        using var exchange = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var upload = PumpAsync(client, target, entry, exchange.Token);
        try
        {
            await RelayResponseAsync(target, client, entry, ct).ConfigureAwait(false);
        }
        finally
        {
            exchange.Cancel();
            await upload.ConfigureAwait(false);
        }
    }

    // Passes the response on with the head rewritten, then the rest as it comes.
    private async Task RelayResponseAsync(Socket target, Socket client, Entry entry, CancellationToken ct)
    {
        var head = ArrayPool<byte>.Shared.Rent(HeadLimit);
        try
        {
            var used = 0;
            var end = 0;
            while (used < HeadLimit)
            {
                var read = await target.ReceiveAsync(head.AsMemory(used, HeadLimit - used), SocketFlags.None, ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                used += read;
                end = HeadEnd(head, used);
                if (end > 0)
                {
                    break;
                }
            }

            if (used > 0)
            {
                await client.SendAsync(Rewrite(head, used, end), SocketFlags.None, ct).ConfigureAwait(false);
                Count(entry, used);
            }
        }
        catch (System.OperationCanceledException)
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
            ArrayPool<byte>.Shared.Return(head);
        }

        await PumpAsync(target, client, entry, ct).ConfigureAwait(false);
    }

    // Copies one direction until end of stream and half-closes the far side.
    private async Task PumpAsync(Socket from, Socket to, Entry entry, CancellationToken ct)
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
                Count(entry, read);
            }
        }
        catch (System.OperationCanceledException)
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

    // Opens the destination: a direct one on a protected socket, everything else on one the tun captures. An
    // undecided destination keeps that state in its entry and is opened the way the application asking for it says.
    private async Task<Socket?> ConnectAsync(Entry entry, int port, string app, CancellationToken ct)
    {
        var addresses = await ResolveAsync(entry, ct).ConfigureAwait(false);
        foreach (var address in addresses)
        {
            var verdict = Classify(entry.Verdict, address);
            if (verdict == RouteVerdict.Block)
            {
                entry.Verdict = RouteVerdict.Block;
                return null;
            }

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            Hold(socket);
            if (Effective(verdict, app) == RouteVerdict.Direct)
            {
                _protect(socket.Handle.ToInt32());
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeoutMs);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token).ConfigureAwait(false);
                entry.Verdict = verdict;
                return socket;
            }
            catch (Exception)
            {
                Close(socket);
            }
        }

        return null;
    }

    // Resolves the name once per idle window; the addresses ride along with the decision they earned.
    private async Task<IReadOnlyList<IPAddress>> ResolveAsync(Entry entry, CancellationToken ct)
    {
        var known = entry.Addresses;
        if (known.Count > 0)
        {
            return known;
        }

        try
        {
            if (IPAddress.TryParse(entry.Host, out var literal))
            {
                IReadOnlyList<IPAddress> single = [literal];
                entry.Addresses = single;
                return single;
            }

            var resolved = await Dns.GetHostAddressesAsync(entry.Host, ct).ConfigureAwait(false);
            var v4 = new List<IPAddress>(resolved.Length);
            foreach (var address in resolved)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    v4.Add(address);
                }
            }

            entry.Addresses = v4;
            return v4;
        }
        catch (Exception)
        {
            return [];
        }
    }

    // Block wins over direct, direct over proxy - the precedence the whole router follows. A name that already
    // decided keeps its verdict; an address only decides what the name left open.
    private RouteVerdict Classify(RouteVerdict byName, IPAddress address)
    {
        if (byName != RouteVerdict.None)
        {
            return byName;
        }

        if (!GeoIpRanges.TryToNumeric(address, out var value))
        {
            return RouteVerdict.None;
        }

        if (_blockRanges.Contains(value))
        {
            return RouteVerdict.Block;
        }

        if (_directRanges.Contains(value))
        {
            return RouteVerdict.Direct;
        }

        return _proxyRanges.Contains(value) ? RouteVerdict.Proxy : RouteVerdict.None;
    }

    private RouteVerdict ByName(string host)
    {
        if (_blockNames.IsTunneled(host))
        {
            return RouteVerdict.Block;
        }

        if (_directNames.IsTunneled(host))
        {
            return RouteVerdict.Direct;
        }

        return _proxyNames.IsTunneled(host) ? RouteVerdict.Proxy : RouteVerdict.None;
    }

    // Takes the entry a destination holds, deciding it on first contact and extending its life on every later one.
    private Entry Touch(string host)
    {
        var entry = _entries.GetOrAdd(host, name =>
        {
            var started = Environment.TickCount64;
            var fresh = new Entry(name) { Verdict = ByName(name), LastTouch = started, Since = started, ReportedAt = started };
            _log($"relay: {name} -> {Word(fresh.Verdict)}");
            return fresh;
        });

        Volatile.Write(ref entry.LastTouch, Environment.TickCount64);
        return entry;
    }

    private void Count(Entry entry, int bytes)
    {
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Add(ref entry.Bytes, bytes);
        Volatile.Write(ref entry.LastTouch, Environment.TickCount64);
    }

    // Drops what nothing has used for the idle window; a destination with a live connection is never dropped.
    private async Task SweepAsync(CancellationToken ct)
    {
        var interval = (int)Math.Clamp(_idleTtlMs / 5, MinSweepMs, MaxSweepMs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }

            var now = Environment.TickCount64;
            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                if (Volatile.Read(ref entry.Live) > 0 || now - Volatile.Read(ref entry.LastTouch) < _idleTtlMs)
                {
                    continue;
                }

                if (_entries.TryRemove(pair.Key, out _))
                {
                    Interlocked.Increment(ref _released);
                    _log($"relay: {pair.Key} released after {_idleTtlMs / 1000} s idle");
                }
            }

            // A cache that outgrows its cap stops being a cache; the coldest go first.
            if (_entries.Count > MaxEntries)
            {
                foreach (var pair in _entries.OrderBy(item => Volatile.Read(ref item.Value.LastTouch)).Take(_entries.Count - MaxEntries))
                {
                    if (Volatile.Read(ref pair.Value.Live) == 0)
                    {
                        _entries.TryRemove(pair.Key, out _);
                    }
                }
            }
        }
    }

    // Names the application behind a loopback connection. While the list names applications the lookup runs per
    // connection, because two of them share a destination and only the owner says where this one belongs; otherwise
    // it costs a system call once per destination, whose answer does not change while the destination is held.
    private string NoteOwner(Socket client, Entry entry)
    {
        if (_owner is null || client.RemoteEndPoint is not IPEndPoint peer
            || (_apps.Count == 0 && entry.App.Length > 0))
        {
            return entry.App;
        }

        var name = _owner(peer);
        if (name is null)
        {
            return entry.App;
        }

        if (entry.App.Length == 0)
        {
            entry.App = name;
        }

        if (_owners.TryAdd(name, 0))
        {
            _log($"relay: {name} -> {entry.Host}");
        }

        return name;
    }

    // Keeps the socket where teardown can reach it.
    private void Hold(Socket socket)
    {
        _open.TryAdd(socket, 0);
    }

    // Closes a socket the exchange is done with.
    private void Close(Socket? socket)
    {
        if (socket is null)
        {
            return;
        }

        _open.TryRemove(socket, out _);
        socket.Dispose();
    }

    // Closes what is still open with a reset: a graceful close waits for an acknowledgement the tun no longer carries,
    // and the socket then stays with the kernel for good.
    private void Drop()
    {
        var count = 0;
        foreach (var pair in _open)
        {
            _open.TryRemove(pair.Key, out _);
            try
            {
                pair.Key.LingerState = new LingerOption(true, 0);
            }
            catch (Exception)
            {
            }

            try
            {
                pair.Key.Dispose();
                count++;
            }
            catch (Exception)
            {
            }
        }

        if (count > 0)
        {
            _log($"relay: {count} connection(s) reset on teardown");
        }
    }

    // Reads the request head into a pooled buffer.
    private static async Task<Request?> ReadRequestAsync(Socket client, CancellationToken ct)
    {
        var head = ArrayPool<byte>.Shared.Rent(HeadLimit);
        try
        {
            var length = await ReadHeadAsync(client, head, HeadLimit, ct).ConfigureAwait(false);
            return length > 0 ? ParseRequest(head, length) : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(head);
        }
    }

    // Reads until the head ends, keeping whatever body arrived with it.
    private static async Task<int> ReadHeadAsync(Socket client, byte[] buffer, int limit, CancellationToken ct)
    {
        var used = 0;
        while (used < limit)
        {
            var read = await client.ReceiveAsync(buffer.AsMemory(used, limit - used), SocketFlags.None, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return 0;
            }

            used += read;
            if (HeadEnd(buffer, used) > 0)
            {
                return used;
            }
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

    // Rewrites a response head to end the connection with the response: a reused one would carry the next request to
    // the destination this one decided, whatever name that request names.
    private static byte[] Rewrite(byte[] raw, int length, int end)
    {
        var lineEnd = end > 0 ? Array.IndexOf(raw, (byte)'\n', 0, end) : -1;
        if (lineEnd <= 0)
        {
            return raw[..length];
        }

        var text = new StringBuilder(Encoding.ASCII.GetString(raw, 0, lineEnd + 1));
        foreach (var header in HeaderLines(raw, lineEnd + 1, end))
        {
            if (!Hop(header))
            {
                text.Append(header).Append("\r\n");
            }
        }

        text.Append("Connection: close\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(text.ToString());
        var rest = raw[end..length];
        var result = new byte[head.Length + rest.Length];
        head.CopyTo(result, 0);
        rest.CopyTo(result, head.Length);
        return result;
    }

    // Splits the head into its header lines, dropping the empty one that ends it.
    private static IEnumerable<string> HeaderLines(byte[] raw, int from, int end)
    {
        foreach (var line in Encoding.ASCII.GetString(raw, from, end - from).Split('\n'))
        {
            var header = line.TrimEnd('\r');
            if (header.Length > 0)
            {
                yield return header;
            }
        }
    }

    // Names the headers that belong to one hop and must not be passed on.
    private static bool Hop(string header)
    {
        return header.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)
            || header.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase)
            || header.StartsWith("Keep-Alive:", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SendAsync(Socket socket, string text, CancellationToken ct)
    {
        await socket.SendAsync(Encoding.ASCII.GetBytes(text), SocketFlags.None, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What one proxied request asks for.
    /// </summary>
    private sealed record Request(string Host, int Port, bool Connect, byte[] Head);

    /// <summary>
    /// What one destination decided and when it was last used.
    /// </summary>
    private sealed class Entry(string host)
    {
        public string Host { get; } = host;

        public RouteVerdict Verdict;
        public long LastTouch;
        public long Since;
        public long Bytes;
        public long Reported;
        public long ReportedAt;
        public int Live;
        public string App = string.Empty;
        public IReadOnlyList<IPAddress> Addresses = [];
    }

    // Takes the destination out of the request line; absolute-form is rewritten to origin-form for the server.
    private static Request? ParseRequest(byte[] raw, int length)
    {
        var lineEnd = Array.IndexOf(raw, (byte)'\n', 0, length);
        if (lineEnd <= 0)
        {
            return null;
        }

        var line = Encoding.ASCII.GetString(raw, 0, lineEnd).TrimEnd('\r', '\n');
        var parts = line.Split(' ');
        if (parts.Length < 3)
        {
            return null;
        }

        if (string.Equals(parts[0], "CONNECT", StringComparison.Ordinal))
        {
            var (name, port) = SplitAuthority(parts[1], 443);
            return name is null ? null : new Request(name, port, true, raw[..length]);
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

        var end = HeadEnd(raw, length);
        if (end <= 0)
        {
            return null;
        }

        var text = new StringBuilder($"{parts[0]} {(slash < 0 ? "/" : rest[slash..])} {parts[2]}\r\n");
        foreach (var header in HeaderLines(raw, lineEnd + 1, end))
        {
            if (!Hop(header))
            {
                text.Append(header).Append("\r\n");
            }
        }

        text.Append("Connection: close\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(text.ToString());
        var tail = raw[end..length];
        var rewritten = new byte[head.Length + tail.Length];
        head.CopyTo(rewritten, 0);
        tail.CopyTo(rewritten, head.Length);
        return new Request(host, tcpPort, false, rewritten);
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

        return int.TryParse(authority[(colon + 1)..], out var port)
            ? (authority[..colon], port)
            : (authority, fallback);
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
        _listener?.Dispose();
        Drop();
        _cts.Dispose();
    }
}
