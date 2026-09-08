using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Tells of every image the machine starts to run, from the process connector of the kernel, as it happens.
/// </summary>
internal sealed class ProcEvents : IDisposable
{
    private const int AfNetlink = 16;
    private const int SockDgram = 2;
    private const int NetlinkConnector = 11;
    private const int AddressBytes = 12;
    private const int GroupsOffset = 8;
    private const uint CnIdxProc = 1;
    private const uint CnValProc = 1;
    private const uint ListenOp = 1;
    private const int NlmsgDone = 3;
    private const uint EventExec = 2;
    private const int NlmsgHeaderBytes = 16;
    private const int ConnectorHeaderBytes = 20;
    private const int ConnectorLengthOffset = 16;
    private const int EventDataOffset = 16;
    private const int DatagramBytes = 8192;

    private readonly Action<int> _executed;
    private readonly AgentLog _log;
    private readonly int _socket;
    private readonly Thread _reader;
    private volatile bool _disposed;

    /// <summary>
    /// ctor
    /// </summary>
    private ProcEvents(int handle, Action<int> executed, AgentLog log)
    {
        _socket = handle;
        _executed = executed;
        _log = log;
        _reader = new Thread(Read) { IsBackground = true, Name = "proc-events" };
    }

    /// <summary>
    /// Subscribes to the exec events; null when the kernel offers no process connector or refuses the subscription.
    /// </summary>
    public static ProcEvents? TryListen(Action<int> executed, AgentLog log)
    {
        var handle = socket(AfNetlink, SockDgram, NetlinkConnector);
        if (handle < 0)
        {
            log.Warn("apps", "the kernel offers no process connector here, so an application shorter than a scan of the process table may miss the tunnel");
            return null;
        }

        var address = new byte[AddressBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(address, AfNetlink);
        BinaryPrimitives.WriteUInt32LittleEndian(address.AsSpan(GroupsOffset), CnIdxProc);
        var subscription = Subscription();
        if (bind(handle, address, address.Length) < 0 || send(handle, subscription, subscription.Length, 0) < 0)
        {
            close(handle);
            log.Warn("apps", "the process connector refused the subscription, so an application shorter than a scan of the process table may miss the tunnel");
            return null;
        }

        var events = new ProcEvents(handle, executed, log);
        events._reader.Start();
        return events;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        close(_socket);
    }

    // The netlink message that asks the connector for the process events.
    private static byte[] Subscription()
    {
        var message = new byte[NlmsgHeaderBytes + ConnectorHeaderBytes + sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)message.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(sizeof(uint)), NlmsgDone);
        var body = message.AsSpan(NlmsgHeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(body, CnIdxProc);
        BinaryPrimitives.WriteUInt32LittleEndian(body[sizeof(uint)..], CnValProc);
        BinaryPrimitives.WriteUInt16LittleEndian(body[ConnectorLengthOffset..], sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(body[ConnectorHeaderBytes..], ListenOp);
        return message;
    }

    // Reads the events until the socket is closed.
    private void Read()
    {
        var buffer = new byte[DatagramBytes];
        while (!_disposed)
        {
            var read = (int)recv(_socket, buffer, buffer.Length, 0);
            if (read <= 0)
            {
                return;
            }

            Dispatch(buffer.AsSpan(0, read));
        }
    }

    // Walks the netlink messages of one datagram.
    private void Dispatch(ReadOnlySpan<byte> datagram)
    {
        var offset = 0;
        while (datagram.Length - offset >= NlmsgHeaderBytes)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(datagram[offset..]);
            if (length < NlmsgHeaderBytes || offset + length > datagram.Length)
            {
                return;
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(datagram[(offset + sizeof(uint))..]) == NlmsgDone)
            {
                Report(datagram.Slice(offset + NlmsgHeaderBytes, length - NlmsgHeaderBytes));
            }

            offset += (length + 3) & ~3;
        }
    }

    // Hands over the pid of one message when it tells of an exec.
    private void Report(ReadOnlySpan<byte> message)
    {
        if (message.Length < ConnectorHeaderBytes + EventDataOffset + (2 * sizeof(int)))
        {
            return;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(message) != CnIdxProc
            || BinaryPrimitives.ReadUInt32LittleEndian(message[sizeof(uint)..]) != CnValProc)
        {
            return;
        }

        var reported = message[ConnectorHeaderBytes..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(reported) != EventExec)
        {
            return;
        }

        var thread = BinaryPrimitives.ReadInt32LittleEndian(reported[EventDataOffset..]);
        var process = BinaryPrimitives.ReadInt32LittleEndian(reported[(EventDataOffset + sizeof(int))..]);
        if (thread != process)
        {
            return;
        }

        try
        {
            _executed(process);
        }
        catch (Exception ex)
        {
            _log.Warn("apps", $"a started application could not be carried: {ex.Message}");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    [DllImport("libc", SetLastError = true)]
    private static extern int bind(int handle, byte[] address, int length);

    [DllImport("libc", SetLastError = true)]
    private static extern nint send(int handle, byte[] buffer, nint length, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern nint recv(int handle, byte[] buffer, nint length, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int handle);
}
