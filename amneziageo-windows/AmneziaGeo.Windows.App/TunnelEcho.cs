using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AmneziaGeo.Geo;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Echoes from the address a tunnel gave the machine, which Windows sends over the adapter of that tunnel alone.
/// </summary>
internal static partial class TunnelEcho
{
    private const uint IpSuccess = 0;

    // Room for ICMP_ECHO_REPLY, the payload and an ICMP error behind it.
    private const int ReplyBytes = 128;

    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>
    /// Returns the echo of the tunnel holding the interface addresses given; null when none of them is IPv4.
    /// </summary>
    public static Func<IPAddress, int, CancellationToken, Task<int>>? From(IReadOnlyList<string> interfaceAddresses)
    {
        var source = Source(interfaceAddresses);
        return source is null ? null : (target, timeoutMs, ct) => RoundTripAsync(source, target, timeoutMs, ct);
    }

    /// <summary>
    /// Returns the first IPv4 address among the interface addresses given.
    /// </summary>
    internal static IPAddress? Source(IReadOnlyList<string> interfaceAddresses)
    {
        return TunnelInbound.Hosts(interfaceAddresses)
            .Select(IPAddress.Parse)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
    }

    /// <summary>
    /// Round trip in milliseconds of an echo sent from the source address; -1 when nothing came back or nothing was sent.
    /// </summary>
    public static Task<int> RoundTripAsync(IPAddress source, IPAddress target, int timeoutMs, CancellationToken ct)
    {
        if (ct.IsCancellationRequested
            || source.AddressFamily != AddressFamily.InterNetwork
            || target.AddressFamily != AddressFamily.InterNetwork)
        {
            return Task.FromResult(-1);
        }

        return Task.Run(() => RoundTrip(Value(source), Value(target), timeoutMs), CancellationToken.None);
    }

    private static int RoundTrip(uint source, uint target, int timeoutMs)
    {
        var handle = IcmpCreateFile();
        if (handle == InvalidHandle)
        {
            return -1;
        }

        try
        {
            var request = 0UL;
            var reply = default(ICMP_ECHO_REPLY);
            var replies = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, source, target, ref request, sizeof(ulong), IntPtr.Zero, ref reply, ReplyBytes, (uint)timeoutMs);
            return replies > 0 && reply.Status == IpSuccess ? (int)reply.RoundTripTime : -1;
        }
        finally
        {
            IcmpCloseHandle(handle);
        }
    }

    // An IPv4 address in the order iphlpapi takes it.
    private static uint Value(IPAddress address)
    {
        return BitConverter.ToUInt32(address.GetAddressBytes());
    }

    // Only the fields this reads are named; the room to spare takes the rest of the answer.
    [StructLayout(LayoutKind.Explicit, Size = ReplyBytes)]
    private struct ICMP_ECHO_REPLY
    {
        [FieldOffset(4)] public uint Status;
        [FieldOffset(8)] public uint RoundTripTime;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial IntPtr IcmpCreateFile();

    [LibraryImport("iphlpapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IcmpCloseHandle(IntPtr handle);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint IcmpSendEcho2Ex(IntPtr handle, IntPtr signal, IntPtr routine, IntPtr context, uint source, uint destination, ref ulong request, ushort requestSize, IntPtr options, ref ICMP_ECHO_REPLY reply, uint replySize, uint timeout);
}
