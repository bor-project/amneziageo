using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Holds the loopback port a carried tunnel dials and passes its datagrams to the carrier process and back.
/// </summary>
internal sealed class CarrierPort : IDisposable
{
    private const int MaxDatagram = 65535;

    // Room each socket keeps for a burst.
    private const int BufferBytes = 2 * 1024 * 1024;

    // Errors in a row after which a loop pauses.
    private const int ErrorRun = 64;
    private const int ErrorPauseMs = 5;

    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private const int AfInet = 2;
    private const int UdpTableOwnerPid = 1;

    // MIB_UDPROW_OWNER_PID: the address, the port in network order, the owning pid.
    private const int RowBytes = 12;
    private const int RowPortOffset = 4;
    private const int RowPidOffset = 8;

    private readonly Socket _tunnelSide;
    private readonly Socket _carrierSide;
    private readonly Func<int, bool> _owned;
    private readonly ILogger _logger;
    private readonly Thread _out;
    private readonly Thread _in;
    private volatile SocketAddress _present;
    private volatile SocketAddress? _beside;
    private volatile SocketAddress? _leaving;
    private volatile SocketAddress? _peer;
    private volatile bool _taken;
    private volatile bool _closed;
    private long _heardAtMs = Environment.TickCount64;
    private bool _refusalLogged;

    /// <summary>
    /// ctor
    /// </summary>
    public CarrierPort(int carrierPort, ILogger logger, Func<int, bool>? owned = null)
    {
        _logger = logger;
        _owned = owned ?? OwnedByThisProcess;
        _present = Address(carrierPort);
        _tunnelSide = Open();
        try
        {
            _carrierSide = Open();
        }
        catch
        {
            _tunnelSide.Dispose();
            throw;
        }

        Port = ((IPEndPoint)_tunnelSide.LocalEndPoint!).Port;
        _out = Run(Out, "carrier port out");
        _in = Run(In, "carrier port in");
        _logger.LogDebug("port {Port} is held for the tunnel and passes its datagrams to the carrier on port {Carrier}", Port, carrierPort);
    }

    /// <summary>
    /// Loopback port the tunnel dials.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Whether the carrier offered has answered.
    /// </summary>
    public bool Taken => _taken;

    /// <summary>
    /// Milliseconds since the present carrier was last heard.
    /// </summary>
    public long QuietMs => Environment.TickCount64 - Volatile.Read(ref _heardAtMs);

    /// <summary>
    /// Passes the datagrams of the tunnel to a second carrier beside the present one.
    /// </summary>
    public void Offer(int carrierPort)
    {
        _taken = false;
        _beside = Address(carrierPort);
    }

    /// <summary>
    /// Stops passing to the carrier offered.
    /// </summary>
    public void Withdraw()
    {
        _beside = null;
    }

    /// <summary>
    /// Makes the carrier offered the present one and keeps hearing the one before.
    /// </summary>
    public void Switch()
    {
        var beside = _beside;
        if (beside is null)
        {
            return;
        }

        _leaving = _present;
        _present = beside;
        _beside = null;
        Volatile.Write(ref _heardAtMs, Environment.TickCount64);
    }

    /// <summary>
    /// Stops hearing the carrier that gave the tunnel over.
    /// </summary>
    public void Forget()
    {
        _leaving = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _tunnelSide.Dispose();
        _carrierSide.Dispose();
        _out.Join(1000);
        _in.Join(1000);
    }

    // Opens a datagram socket on the loopback that a port nobody holds does not reset.
    private static Socket Open()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.ReceiveBufferSize = BufferBytes;
            socket.SendBufferSize = BufferBytes;
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            if (OperatingSystem.IsWindows())
            {
                socket.IOControl(SioUdpConnReset, new byte[4], null);
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static SocketAddress Address(int port)
    {
        return new IPEndPoint(IPAddress.Loopback, port).Serialize();
    }

    private static Thread Run(ThreadStart body, string name)
    {
        var thread = new Thread(body) { IsBackground = true, Name = name };
        thread.Start();
        return thread;
    }

    // Passes the datagrams of the tunnel to the carrier.
    private void Out()
    {
        var buffer = new byte[MaxDatagram];
        var from = new SocketAddress(AddressFamily.InterNetwork);
        var errors = 0;
        while (!_closed)
        {
            try
            {
                var received = _tunnelSide.ReceiveFrom(buffer.AsSpan(), SocketFlags.None, from);
                if (FromTunnel(from))
                {
                    var beside = _beside;
                    if (beside is not null)
                    {
                        _carrierSide.SendTo(buffer.AsSpan(0, received), SocketFlags.None, beside);
                    }

                    _carrierSide.SendTo(buffer.AsSpan(0, received), SocketFlags.None, _present);
                }

                errors = 0;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                if (_closed)
                {
                    return;
                }

                Pause(ref errors);
            }
        }
    }

    // Passes the datagrams of the carrier to the tunnel.
    private void In()
    {
        var buffer = new byte[MaxDatagram];
        var from = new SocketAddress(AddressFamily.InterNetwork);
        var errors = 0;
        while (!_closed)
        {
            try
            {
                var received = _carrierSide.ReceiveFrom(buffer.AsSpan(), SocketFlags.None, from);
                var peer = _peer;
                if (FromCarrier(from) && peer is not null)
                {
                    _tunnelSide.SendTo(buffer.AsSpan(0, received), SocketFlags.None, peer);
                }

                errors = 0;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                if (_closed)
                {
                    return;
                }

                Pause(ref errors);
            }
        }
    }

    // Tells whether a datagram came from the tunnel, taking a new port of the tunnel for the old one.
    private bool FromTunnel(SocketAddress from)
    {
        var peer = _peer;
        if (peer is not null && Same(peer, from))
        {
            return true;
        }

        var port = (from[2] << 8) | from[3];
        if (!_owned(port))
        {
            Refused(port);
            return false;
        }

        var copy = new SocketAddress(from.Family, from.Size);
        from.Buffer.Span[..from.Size].CopyTo(copy.Buffer.Span);
        _peer = copy;
        if (peer is not null)
        {
            _logger.LogInformation("the tunnel now sends from port {Port}, so the carrier's answers go there", port);
        }

        return true;
    }

    // Tells whether a datagram came from a carrier of the tunnel, noting which one.
    private bool FromCarrier(SocketAddress from)
    {
        if (Same(from, _present))
        {
            Volatile.Write(ref _heardAtMs, Environment.TickCount64);
            return true;
        }

        var beside = _beside;
        if (beside is not null && Same(from, beside))
        {
            _taken = true;
            return true;
        }

        var leaving = _leaving;
        return leaving is not null && Same(from, leaving);
    }

    // Writes the first datagram of a stranger to the journal.
    private void Refused(int port)
    {
        if (_refusalLogged)
        {
            return;
        }

        _refusalLogged = true;
        _logger.LogWarning("a datagram came to port {Own} from port {Port}, which the tunnel does not hold, and was dropped; later ones are dropped without a line", Port, port);
    }

    // Tells whether two IPv4 socket addresses name one port of one host.
    private static bool Same(SocketAddress one, SocketAddress other)
    {
        for (var index = 2; index < 8; index++)
        {
            if (one[index] != other[index])
            {
                return false;
            }
        }

        return true;
    }

    // Waits out a run of errors.
    private static void Pause(ref int errors)
    {
        if (++errors >= ErrorRun)
        {
            Thread.Sleep(ErrorPauseMs);
        }
    }

    // Tells whether a UDP port belongs to this process.
    private static bool OwnedByThisProcess(int port)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var size = 0;
        _ = GetExtendedUdpTable(IntPtr.Zero, ref size, false, AfInet, UdpTableOwnerPid, 0);
        if (size <= 0)
        {
            return true;
        }

        var table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(table, ref size, false, AfInet, UdpTableOwnerPid, 0) != 0)
            {
                return true;
            }

            var pid = Environment.ProcessId;
            var count = Marshal.ReadInt32(table);
            for (var index = 0; index < count; index++)
            {
                var row = table + 4 + (index * RowBytes);
                var held = (Marshal.ReadByte(row, RowPortOffset) << 8) | Marshal.ReadByte(row, RowPortOffset + 1);
                if (held == port && Marshal.ReadInt32(row, RowPidOffset) == pid)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, int reserved);
}
