using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Carries a tunnel's AmneziaWG UDP over a wstunnel WebSocket (TCP/TLS) so the tunnel works on networks that block UDP.
/// </summary>
internal sealed class WsTunnelTransport : IAsyncDisposable
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const int TcpStateEstablished = 5;

    // TCP_ESTATS_TYPE: the data counters and the path counters.
    private const int EstatsData = 1;
    private const int EstatsPath = 3;

    // sizeof(TCP_ESTATS_DATA_ROD_v0) and sizeof(TCP_ESTATS_PATH_ROD_v0); a size the kernel does not recognise is
    // refused outright rather than filled in part.
    private const int DataRodBytes = 96;
    private const int PathRodBytes = 160;

    // DataBytesOut and BytesRetrans within those two.
    private const int BytesOutOffset = 0;
    private const int BytesRetransOffset = 24;

    // Lines of the carrier's stderr kept for the log of a carrier that dies before it listens.
    private const int StderrTail = 20;

    private readonly string _serverHost;
    private readonly int _wsPort;
    // How often the headers are written anew, well inside the window the server takes a token in.
    private static readonly TimeSpan HeadersRefresh = TimeSpan.FromSeconds(30);

    // How long a carrier started beside the present one may take to listen, and then to carry.
    private static readonly TimeSpan HandoverListen = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HandoverCarry = TimeSpan.FromSeconds(8);

    // How long the carrier that gave the tunnel over is still heard.
    private static readonly TimeSpan HandoverSettle = TimeSpan.FromMilliseconds(500);

    // Silence of the present carrier past which it no longer counts as carrying.
    private const int CarriesMs = 3000;

    // Life of a carrier under which its fall counts as a failed start.
    private const int FailedStartMs = 10_000;

    private readonly int _targetPort;   // server-side AmneziaWG UDP port (original Endpoint port)
    private readonly CarrierPort _port; // the port the tunnel dials in front of the carrier
    private volatile int _carrierPort;  // loopback UDP port the carrier process listens on
    private readonly string _pathPrefix; // path token for server-side --restrict-http-upgrade-path-prefix
    private readonly string _credentials; // optional basic-auth "user[:pass]"
    private readonly Func<string>? _header; // the token header the front of a server of ours asks for
    private readonly string _headersFile; // the file wstunnel reads the headers from on every connection
    private readonly Action<string>? _onRejected;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<string> _stderr = new();
    private readonly object _exitLock = new();
    private (int Code, string Stderr)? _lastExit;
    private Process? _process;
    private Process? _leaving;
    private Task? _supervisor;
    private Task? _refresher;
    private Task? _handover;
    private int _rejectionReported;
    private int _redials;
    private int _handing;
    private long _startedAtMs;

    private WsTunnelTransport(string serverHost, int wsPort, int targetPort, string pathPrefix, string credentials, Func<string>? header, string headersFile, int carrierPort, Action<string>? onRejected, ILogger logger)
    {
        _serverHost = serverHost;
        _wsPort = wsPort;
        _targetPort = targetPort;
        _pathPrefix = pathPrefix;
        _credentials = credentials;
        _header = header;
        _headersFile = headersFile;
        _carrierPort = carrierPort;
        _port = new CarrierPort(carrierPort, logger);
        LocalPort = _port.Port;
        _onRejected = onRejected;
        _logger = logger;
    }

    /// <summary>
    /// Loopback UDP port the WG engine dials instead of the blocked public endpoint.
    /// </summary>
    public int LocalPort { get; }

    /// <summary>
    /// How many times the carrier has been re-dialled during this session.
    /// </summary>
    public int Redials => Volatile.Read(ref _redials);

    /// <summary>
    /// Starts a fresh carrier beside the present one and hands the tunnel over once it carries.
    /// </summary>
    public void Redial(string reason)
    {
        if (_process is null || Interlocked.CompareExchange(ref _handing, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _redials);
        _logger.LogWarning("the websocket carrier is being re-dialled ({Reason}); a fresh one is started beside it and takes the tunnel over once it carries, so the tunnel keeps its session and its traffic", reason);
        _handover = Task.Run(() => HandOverAsync(_cts.Token));
    }

    // Hands the tunnel over to a carrier started beside the present one.
    private async Task HandOverAsync(CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var old = _process;
        var (fresh, port) = LaunchBeside();
        var adopted = false;
        try
        {
            if (fresh is null || !await WaitUntilListeningAsync(port, HandoverListen, ct).ConfigureAwait(false))
            {
                _logger.LogWarning("a fresh websocket carrier did not start beside the present one, so the present one is stopped and started again in its place");
                Stop(old);
                return;
            }

            _port.Offer(port);
            var taken = await TakenAsync(old, ct).ConfigureAwait(false);
            if (!taken && Running(old) && _port.QuietMs < CarriesMs)
            {
                _port.Withdraw();
                _logger.LogWarning("the fresh websocket carrier carried nothing in {Seconds} s while the present one still does, so the present one stays", (int)HandoverCarry.TotalSeconds);
                return;
            }

            _port.Switch();
            _carrierPort = port;
            _leaving = old;
            _process = fresh;
            Volatile.Write(ref _startedAtMs, Environment.TickCount64);
            adopted = true;
            _logger.LogInformation("the fresh websocket carrier (process {Pid}) has the tunnel after {Elapsed} ms{Outcome}; the one before is stopped",
                fresh.Id, started.ElapsedMilliseconds, taken ? string.Empty : ", though nothing has come through it yet");
            await Task.Delay(HandoverSettle, ct).ConfigureAwait(false);
            Stop(old);
            _port.Forget();
            _leaving = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or SocketException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "handing the tunnel over to a fresh websocket carrier failed");
        }
        finally
        {
            if (!adopted)
            {
                Stop(fresh);
                fresh?.Dispose();
            }

            Volatile.Write(ref _handing, 0);
        }
    }

    // Starts a carrier on a port of its own; no process when it does not start.
    private (Process? Process, int Port) LaunchBeside()
    {
        try
        {
            var port = FreeUdpPort();
            return (Launch(port), port);
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "the loopback gave no port for a fresh websocket carrier");
            return (null, 0);
        }
    }

    // Waits until the carrier offered answers, the present one exits or the time is up.
    private async Task<bool> TakenAsync(Process? present, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + HandoverCarry;
        while (!_port.Taken && Running(present) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20, ct).ConfigureAwait(false);
        }

        return _port.Taken;
    }

    private static bool Running(Process? process)
    {
        return process is not null && TryGetPid(process) != 0;
    }

    // Stops a carrier process.
    private void Stop(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "a websocket carrier process would not stop");
        }
    }

    /// <summary>
    /// TCP connections the carrier process holds, and how many of them are established. A carrier with no
    /// established connection carries nothing, whatever its process is doing.
    /// </summary>
    public (int Total, int Established) Sessions()
    {
        var process = _process;
        if (process is null)
        {
            return (0, 0);
        }

        var pid = TryGetPid(process);
        return pid == 0 ? (0, 0) : CountSessions(pid);
    }

    /// <summary>
    /// Bytes the carrier's established connections have sent, and how many of them the network made it send
    /// again. A carrier deep in retransmission still passes the small packets a session lives on while a
    /// transfer inside it never finishes, which no other counter here shows.
    /// </summary>
    public (long BytesOut, long BytesRetrans) Retransmission()
    {
        var process = _process;
        if (process is null)
        {
            return (0, 0);
        }

        var pid = TryGetPid(process);
        return pid == 0 ? (0, 0) : CountWire(pid);
    }

    private static uint TryGetPid(Process process)
    {
        try
        {
            return process.HasExited ? 0 : (uint)process.Id;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static (int Total, int Established) CountSessions(uint pid)
    {
        var rows = Rows(pid);
        var established = 0;
        foreach (var row in rows)
        {
            if (row.State == TcpStateEstablished)
            {
                established++;
            }
        }

        return (rows.Count, established);
    }

    // Per-connection statistics Windows keeps once collection is turned on for that connection; enabling it
    // again on one already collecting costs nothing and covers a carrier that has re-dialled since.
    private static (long BytesOut, long BytesRetrans) CountWire(uint pid)
    {
        var sent = 0L;
        var again = 0L;
        var enable = new byte[] { 1 };
        var data = new byte[DataRodBytes];
        var path = new byte[PathRodBytes];
        foreach (var row in Rows(pid))
        {
            if (row.State != TcpStateEstablished)
            {
                continue;
            }

            var one = row;
            _ = SetPerTcpConnectionEStats(ref one, EstatsData, enable, 0, 1, 0);
            _ = SetPerTcpConnectionEStats(ref one, EstatsPath, enable, 0, 1, 0);
            if (GetPerTcpConnectionEStats(ref one, EstatsData, null, 0, 0, null, 0, 0, data, 0, DataRodBytes) == 0)
            {
                sent += (long)BitConverter.ToUInt64(data, BytesOutOffset);
            }

            if (GetPerTcpConnectionEStats(ref one, EstatsPath, null, 0, 0, null, 0, 0, path, 0, PathRodBytes) == 0)
            {
                again += BitConverter.ToUInt32(path, BytesRetransOffset);
            }
        }

        return (sent, again);
    }

    // The connections one process holds.
    private static List<TcpRow> Rows(uint pid)
    {
        var rows = new List<TcpRow>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return rows;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
            {
                return rows;
            }

            var count = Marshal.ReadInt32(buffer);
            var basePtr = buffer + 4;
            for (var i = 0; i < count; i++)
            {
                // MIB_TCPROW_OWNER_PID: MIB_TCPROW itself, then the owning pid at 20, each row 24 bytes.
                var row = basePtr + (i * 24);
                if ((uint)Marshal.ReadInt32(row, 20) == pid)
                {
                    rows.Add(Marshal.PtrToStructure<TcpRow>(row));
                }
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Starts a wstunnel client and waits until its local UDP listener is bound; null on missing binary or timeout.
    /// The callback fires once when the carrier reports a permanent rejection (TLS certificate).
    /// </summary>
    public static async Task<WsTunnelTransport?> StartAsync(string serverHost, int wsPort, int targetPort, string pathPrefix, string credentials, Func<string>? header, string headersFile, Action<string>? onRejected, ILogger logger, CancellationToken ct)
    {
        var exe = TunnelPaths.WsTunnelExe();
        if (!File.Exists(exe))
        {
            logger.LogError("this configuration asks to be carried inside a websocket, but the program that does it is missing ({Exe}); the connection cannot start - reinstall the app", exe);
            return null;
        }

        var transport = Create(serverHost, wsPort, targetPort, pathPrefix, credentials, header, headersFile, onRejected, logger);
        if (transport is null)
        {
            return null;
        }

        transport.WriteHeaders();
        transport.Spawn();
        transport._supervisor = Task.Run(() => transport.SuperviseAsync(transport._cts.Token));
        transport._refresher = Task.Run(() => transport.RefreshAsync(transport._cts.Token));

        if (await WaitUntilListeningAsync(transport._carrierPort, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false))
        {
            return transport;
        }

        if (transport.LastExit() is { } exit)
        {
            logger.LogError("the websocket carrier exited with code 0x{Code:X8} before it listened on port {Port}, so the tunnel has nothing to dial; the connect is aborted. Its stderr: {Stderr}", exit.Code, transport._carrierPort, exit.Stderr);
        }
        else
        {
            logger.LogError("the websocket carrier never started listening on port {Port}, so the tunnel has nothing to dial; the connect is aborted", transport._carrierPort);
        }

        await transport.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    // Makes the transport with the port the tunnel dials; null when the loopback gives none.
    private static WsTunnelTransport? Create(string serverHost, int wsPort, int targetPort, string pathPrefix, string credentials, Func<string>? header, string headersFile, Action<string>? onRejected, ILogger logger)
    {
        try
        {
            // The port of the carrier stays taken until the port in front of it is bound.
            using var held = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            held.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return new WsTunnelTransport(serverHost, wsPort, targetPort, pathPrefix, credentials, header, headersFile, ((IPEndPoint)held.LocalEndPoint!).Port, onRejected, logger);
        }
        catch (SocketException ex)
        {
            logger.LogError(ex, "the loopback gave no port for the tunnel to dial its websocket carrier at, so the connection cannot start");
            return null;
        }
    }

    // How the last carrier process ended and what it wrote to stderr; null while none has exited.
    private (int Code, string Stderr)? LastExit()
    {
        lock (_exitLock)
        {
            return _lastExit;
        }
    }

    // Keeps the last lines of the carrier's stderr.
    private void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_exitLock)
        {
            _stderr.Enqueue(line);
            while (_stderr.Count > StderrTail)
            {
                _stderr.Dequeue();
            }
        }
    }

    private void Spawn()
    {
        _process = Launch(_carrierPort);
        Volatile.Write(ref _startedAtMs, Environment.TickCount64);
    }

    // Starts a carrier process that listens on the loopback port given; null when it does not start.
    private Process? Launch(int port)
    {
        // -L udp://<port>:127.0.0.1:<targetPort> forwards to the AmneziaWG interface on the server;
        // timeout_sec=0 keeps the UDP association alive. The token in the headers file proves the keys of the
        // configuration, so the certificate is taken as it stands; without a token it is verified. Optional -P path
        // token and basic-auth credentials.
        var auth = _header is null
            ? " --tls-verify-certificate"
            : $" --http-headers-file \"{_headersFile}\"";
        if (_pathPrefix.Length > 0)
        {
            auth += $" -P \"{_pathPrefix}\"";
        }

        if (_credentials.Length > 0)
        {
            auth += $" --http-upgrade-credentials \"{_credentials}\"";
        }

        var args = $"client{auth} -L \"udp://{port}:127.0.0.1:{_targetPort}?timeout_sec=0\" \"wss://{_serverHost}:{_wsPort}\"";
        var info = new ProcessStartInfo(TunnelPaths.WsTunnelExe(), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        lock (_exitLock)
        {
            _stderr.Clear();
        }

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "the websocket carrier could not be launched; this connection cannot be disguised as web traffic and will not come up");
            return null;
        }

        if (process is null)
        {
            _logger.LogError("the websocket carrier did not start and reported no reason; this connection will not come up");
            return null;
        }

        process.OutputDataReceived += (_, e) => Trace(e.Data);
        process.ErrorDataReceived += (_, e) =>
        {
            Remember(e.Data);
            Trace(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _logger.LogInformation(
            "the websocket carrier is running (process {Pid}): it takes the tunnel from local port {Local}, wraps it in an encrypted web connection to {Host}:{Ws}, and the server hands it to port {Target}",
            process.Id, port, _serverHost, _wsPort, _targetPort);
        return process;
    }

    // wstunnel carries its own level in every line; keep that level instead of burying the whole stream at Debug,
    // where the cause of a refused carrier never reaches the journal.
    private void Trace(string? line)
    {
        if (line is null)
        {
            return;
        }

        if (IsRejection(line))
        {
            _logger.LogError("the websocket carrier says: {Line}", line);
            ReportRejection(line);
            return;
        }

        if (line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("WARN", StringComparison.Ordinal))
        {
            _logger.LogWarning("the websocket carrier says: {Line}", line);
            return;
        }

        _logger.LogInformation("the websocket carrier says: {Line}", line);
    }

    // A refused certificate is permanent: an expired or untrusted server cert never clears by re-dialing.
    private static bool IsRejection(string line)
    {
        if (line.Contains("invalid peer certificate", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!line.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return line.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || line.Contains("unknown issuer", StringComparison.OrdinalIgnoreCase)
            || line.Contains("unknownissuer", StringComparison.OrdinalIgnoreCase)
            || line.Contains("not valid", StringComparison.OrdinalIgnoreCase)
            || line.Contains("notvalidfor", StringComparison.OrdinalIgnoreCase)
            || line.Contains("verify failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("bad certificate", StringComparison.OrdinalIgnoreCase);
    }

    private void ReportRejection(string line)
    {
        if (Interlocked.Exchange(ref _rejectionReported, 1) != 0)
        {
            return;
        }

        try
        {
            _onRejected?.Invoke(line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "the carrier's refusal could not be recorded, so this attempt may be reported as an unreachable server instead of naming the real cause");
        }
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var process = _process;
            if (process is null)
            {
                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                Spawn();
                continue;
            }

            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            // Waits out a handover and holds off another one while the carrier is started again.
            try
            {
                while (Interlocked.CompareExchange(ref _handing, 1, 0) != 0)
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                // A carrier that gave the tunnel over has nothing to start again.
                if (!ReferenceEquals(process, _process))
                {
                    process.Dispose();
                    continue;
                }

                var code = process.ExitCode;
                lock (_exitLock)
                {
                    _lastExit = (code, _stderr.Count == 0 ? "(empty)" : string.Join(Environment.NewLine, _stderr));
                }

                var failed = Environment.TickCount64 - Volatile.Read(ref _startedAtMs) < FailedStartMs;
                _logger.LogWarning("the websocket carrier stopped (exit code {Code}); traffic is interrupted until it is started again on port {Port}, {When}", code, _carrierPort, failed ? "in a second" : "at once");
                process.Dispose();
                _process = null;

                try
                {
                    await Task.Delay(failed ? 1000 : 0, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                Spawn();
            }
            finally
            {
                Volatile.Write(ref _handing, 0);
            }
        }
    }

    // Writes a fresh token header for the next connection wstunnel opens.
    private void WriteHeaders()
    {
        if (_header is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(_headersFile, $"Authorization: {_header()}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "the headers of the websocket carrier could not be written to {File}; a new websocket is refused once the token they hold has aged", _headersFile);
        }
    }

    // Keeps the token in the headers file inside the window the server takes it in.
    private async Task RefreshAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeadersRefresh, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            WriteHeaders();
        }
    }

    private static async Task<bool> WaitUntilListeningAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            // wstunnel binds its local UDP socket on start; the WS connection opens lazily on the first datagram.
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners();
            foreach (var endpoint in listeners)
            {
                if (endpoint.Port == port && (IPAddress.IsLoopback(endpoint.Address) || endpoint.Address.Equals(IPAddress.Any)))
                {
                    return true;
                }
            }

            try
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_supervisor is not null)
        {
            try
            {
                await _supervisor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_refresher is not null)
        {
            await _refresher.ConfigureAwait(false);
        }

        if (_handover is not null)
        {
            await _handover.ConfigureAwait(false);
        }

        var leaving = _leaving;
        if (leaving is not null)
        {
            Stop(leaving);
            leaving.Dispose();
            _leaving = null;
        }

        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            process.Dispose();
            _process = null;
        }

        _port.Dispose();
        _cts.Dispose();
        if (_header is not null)
        {
            try
            {
                File.Delete(_headersFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "the headers of the websocket carrier stay in {File}", _headersFile);
            }
        }

        _logger.LogInformation("the websocket carrier is stopped");
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, int reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetPerTcpConnectionEStats(ref TcpRow row, int estatsType, byte[] rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetPerTcpConnectionEStats(ref TcpRow row, int estatsType, byte[]? rw, uint rwVersion, uint rwSize, byte[]? ros, uint rosVersion, uint rosSize, byte[] rod, uint rodVersion, uint rodSize);

    // MIB_TCPROW: the five fields that name one connection, its ports in network order.
    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
    }
}
