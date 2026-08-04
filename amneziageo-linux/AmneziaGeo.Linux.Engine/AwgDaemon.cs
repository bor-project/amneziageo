using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace AmneziaGeo.Linux.Engine;

/// <summary>
/// Drives the standalone amneziawg-go daemon over its UAPI unix socket.
/// </summary>
public sealed class AwgDaemon : IDisposable
{
    // amneziawg-go control-socket directory (ipc/uapi_unix.go).
    private const string SocketDirectory = "/var/run/amneziawg";
    private const int Sigterm = 15;
    private const int ShutdownGraceMs = 3000;

    private readonly string _binary;
    private readonly string _iface;
    private Process? _process;
    private bool _owns;
    private bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    public AwgDaemon(string binaryPath, string interfaceName)
    {
        _binary = binaryPath;
        _iface = interfaceName;
    }

    /// <summary>
    /// Control-socket path of the interface.
    /// </summary>
    public string SocketPath => $"{SocketDirectory}/{_iface}.sock";

    /// <summary>
    /// Whether the daemon process is alive.
    /// </summary>
    public bool Running => _process is { HasExited: false };

    /// <summary>
    /// Launches the daemon in the foreground for the interface.
    /// </summary>
    public void Start()
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        if (SocketAnswers())
        {
            throw new InvalidOperationException($"another amneziawg-go already serves {_iface} on {SocketPath}");
        }

        ClearStaleSocket();

        var info = new ProcessStartInfo
        {
            FileName = _binary,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-f");
        info.ArgumentList.Add(_iface);
        _process = Process.Start(info);
        _owns = _process is not null;
    }

    /// <summary>
    /// Applies a UAPI configuration to the running interface.
    /// </summary>
    public async Task ConfigureAsync(string uapiConfig, CancellationToken ct = default)
    {
        var request = new StringBuilder("set=1\n").Append(uapiConfig);
        if (!uapiConfig.EndsWith('\n'))
        {
            request.Append('\n');
        }

        request.Append('\n');

        var reply = await RoundtripAsync(request.ToString(), ct).ConfigureAwait(false);
        var errno = ParseErrno(reply);
        if (errno != 0)
        {
            throw new IOException($"amneziawg-go set failed: errno {errno}");
        }
    }

    /// <summary>
    /// Adds one address range to a peer, keeping the ranges it already carries.
    /// </summary>
    public async Task AddAllowedIpAsync(string peerPublicKeyHex, string cidr, CancellationToken ct = default)
    {
        var reply = await RoundtripAsync($"set=1\npublic_key={peerPublicKeyHex}\nallowed_ip={cidr}\n\n", ct).ConfigureAwait(false);
        var errno = ParseErrno(reply);
        if (errno != 0)
        {
            throw new IOException($"amneziawg-go allowed_ip add failed: errno {errno}");
        }
    }

    /// <summary>
    /// Reads the running UAPI configuration of the interface.
    /// </summary>
    public Task<string> GetConfigAsync(CancellationToken ct = default) => RoundtripAsync("get=1\n\n", ct);

    /// <summary>
    /// Stops the daemon, leaving it time to remove its control socket.
    /// </summary>
    public void Stop()
    {
        if (_process is { HasExited: false } process)
        {
            kill(process.Id, Sigterm);
            if (!process.WaitForExit(ShutdownGraceMs))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(ShutdownGraceMs);
            }
        }

        // Only a socket this instance opened is removed: deleting one that belongs to another daemon takes its
        // live tunnel down with it.
        if (_owns)
        {
            _owns = false;
            DeleteSocket();
        }
    }

    // A daemon that did not shut down cleanly leaves its control socket behind, and the next start would
    // then reach a socket nothing answers on.
    private void ClearStaleSocket()
    {
        if (File.Exists(SocketPath) && !SocketAnswers())
        {
            DeleteSocket();
        }
    }

    // Whether a daemon is listening on the control socket.
    private bool SocketAnswers()
    {
        if (!File.Exists(SocketPath))
        {
            return false;
        }

        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(SocketPath));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private void DeleteSocket()
    {
        try
        {
            File.Delete(SocketPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Sends one UAPI request over the control socket and returns the full reply.
    private async Task<string> RoundtripAsync(string request, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct).ConfigureAwait(false);
        await socket.SendAsync(Encoding.UTF8.GetBytes(request), SocketFlags.None, ct).ConfigureAwait(false);

        var response = new StringBuilder();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            response.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (response.ToString().EndsWith("\n\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        return response.ToString();
    }

    // Reads the errno line from a UAPI reply; -1 when absent.
    private static int ParseErrno(string reply)
    {
        foreach (var line in reply.Split('\n'))
        {
            if (line.StartsWith("errno=", StringComparison.Ordinal) && int.TryParse(line.AsSpan(6), out var value))
            {
                return value;
            }
        }

        return -1;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _process?.Dispose();
    }
}
