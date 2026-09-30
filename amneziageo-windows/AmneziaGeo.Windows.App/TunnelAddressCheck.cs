using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Keeps the tunnel adapter's addresses out of the duplicate address check.
/// </summary>
internal static partial class TunnelAddressCheck
{
    private const ushort AfUnspec = 0;
    private const ushort AfInet = 2;
    private const ushort AfInet6 = 23;
    private const uint NoError = 0;
    private const int DadTentative = 1;
    private const int DadPreferred = 4;

    // Where the rows of an address table start: after its count, at the alignment of a row.
    private const int TableRows = 8;

    private static readonly TimeSpan _look = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan _adapterLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _addressLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Turns the check off on the adapter as soon as it appears and adds again the addresses that entered it before.
    /// </summary>
    public static async Task RunAsync(string name, string adapter, IReadOnlyList<IPAddress> expected, ILogger logger, CancellationToken ct)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            if (await AdapterAsync(adapter, ct).ConfigureAwait(false) is not { } luid)
            {
                return;
            }

            var found = watch.ElapsedMilliseconds;
            var v4 = !expected.Any(address => address.AddressFamily == AddressFamily.InterNetwork);
            var v6 = !expected.Any(address => address.AddressFamily == AddressFamily.InterNetworkV6);
            var added = 0;
            var ready = false;
            var deadline = watch.Elapsed + _addressLimit;
            while (!ready && watch.Elapsed < deadline)
            {
                v4 = v4 || SkipCheck(luid, AfInet);
                v6 = v6 || SkipCheck(luid, AfInet6);
                foreach (var address in Tentative(Present(luid), expected))
                {
                    added += AddAgain(luid, address) ? 1 : 0;
                }

                ready = v4 && v6 && Settled(Present(luid), expected);
                if (!ready)
                {
                    await Task.Delay(_look, ct).ConfigureAwait(false);
                }
            }

            if (added > 0)
            {
                logger.LogInformation("{Name}: {Count} address(es) of the tunnel were already in the duplicate address check, so they were added again without it", name, added);
            }

            logger.LogDebug("{Name}: the tunnel adapter skips the duplicate address check: it appeared after {Found} ms, its addresses were {State} after {Elapsed} ms",
                name, found, ready ? "ready" : "not all ready", watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name}: the tunnel's addresses could not be kept out of the duplicate address check, so they may carry nothing for a few seconds", name);
        }
    }

    /// <summary>
    /// The addresses a configuration gives the adapter.
    /// </summary>
    internal static IReadOnlyList<IPAddress> Expected(IEnumerable<string> addresses)
    {
        var result = new List<IPAddress>();
        foreach (var entry in addresses)
        {
            var slash = entry.IndexOf('/');
            if (IPAddress.TryParse(slash < 0 ? entry.Trim() : entry[..slash].Trim(), out var address))
            {
                result.Add(address);
            }
        }

        return result;
    }

    /// <summary>
    /// The configured addresses still in the duplicate address check.
    /// </summary>
    internal static IReadOnlyList<IPAddress> Tentative(IReadOnlyList<(IPAddress Address, int DadState)> present, IReadOnlyList<IPAddress> expected)
    {
        return [.. present.Where(row => row.DadState == DadTentative && expected.Contains(row.Address)).Select(row => row.Address)];
    }

    /// <summary>
    /// Whether every configured address is on the adapter and out of the duplicate address check.
    /// </summary>
    internal static bool Settled(IReadOnlyList<(IPAddress Address, int DadState)> present, IReadOnlyList<IPAddress> expected)
    {
        return expected.All(address => present.Any(row => row.Address.Equals(address) && row.DadState != DadTentative));
    }

    // The adapter's LUID once it is there; null when it never appears.
    private static async Task<ulong?> AdapterAsync(string adapter, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < _adapterLimit)
        {
            if (ConvertInterfaceAliasToLuid(adapter, out var luid) == NoError)
            {
                return luid;
            }

            await Task.Delay(_look, ct).ConfigureAwait(false);
        }

        return null;
    }

    // Sets no duplicate probes for the family; answers whether the adapter has the family and took it.
    private static bool SkipCheck(ulong luid, ushort family)
    {
        var row = new MIB_IPINTERFACE_ROW { Family = family, InterfaceLuid = luid };
        if (GetIpInterfaceEntry(ref row) != NoError)
        {
            return false;
        }

        if (row.DadTransmits == 0)
        {
            return true;
        }

        row.DadTransmits = 0;
        if (family == AfInet)
        {
            row.SitePrefixLength = 0;
        }

        return SetIpInterfaceEntry(ref row) == NoError;
    }

    // The adapter's addresses with their duplicate check states.
    private static List<(IPAddress Address, int DadState)> Present(ulong luid)
    {
        var result = new List<(IPAddress Address, int DadState)>();
        if (GetUnicastIpAddressTable(AfUnspec, out var table) != NoError)
        {
            return result;
        }

        try
        {
            var count = Marshal.ReadInt32(table);
            var size = Marshal.SizeOf<MIB_UNICASTIPADDRESS_ROW>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_UNICASTIPADDRESS_ROW>(table + TableRows + (i * size));
                if (row.InterfaceLuid == luid && ToAddress(row.Address) is { } address)
                {
                    result.Add((address, row.DadState));
                }
            }
        }
        finally
        {
            FreeMibTable(table);
        }

        return result;
    }

    // Adds a tentative address again, marked as already checked.
    private static bool AddAgain(ulong luid, IPAddress address)
    {
        var row = new MIB_UNICASTIPADDRESS_ROW { Address = ToSockaddr(address), InterfaceLuid = luid };
        if (GetUnicastIpAddressEntry(ref row) != NoError || row.DadState != DadTentative)
        {
            return false;
        }

        var checkedRow = Fresh(row);
        checkedRow.DadState = DadPreferred;
        if (DeleteUnicastIpAddressEntry(ref row) != NoError)
        {
            return false;
        }

        if (CreateUnicastIpAddressEntry(ref checkedRow) == NoError)
        {
            return true;
        }

        var plain = Fresh(row);
        CreateUnicastIpAddressEntry(ref plain);
        return false;
    }

    // A new row for the address the given one holds.
    private static MIB_UNICASTIPADDRESS_ROW Fresh(MIB_UNICASTIPADDRESS_ROW row)
    {
        InitializeUnicastIpAddressEntry(out var fresh);
        fresh.Address = row.Address;
        fresh.InterfaceLuid = row.InterfaceLuid;
        fresh.OnLinkPrefixLength = row.OnLinkPrefixLength;
        fresh.SkipAsSource = row.SkipAsSource;
        return fresh;
    }

    private static SOCKADDR_INET ToSockaddr(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return new SOCKADDR_INET { Family = AfInet, V4 = BitConverter.ToUInt32(bytes, 0) };
        }

        return new SOCKADDR_INET
        {
            Family = AfInet6,
            V6a = BitConverter.ToUInt32(bytes, 0),
            V6b = BitConverter.ToUInt32(bytes, 4),
            V6c = BitConverter.ToUInt32(bytes, 8),
            V6d = BitConverter.ToUInt32(bytes, 12),
            ScopeId = (uint)address.ScopeId,
        };
    }

    private static IPAddress? ToAddress(SOCKADDR_INET address)
    {
        if (address.Family == AfInet)
        {
            return new IPAddress(BitConverter.GetBytes(address.V4));
        }

        if (address.Family != AfInet6)
        {
            return null;
        }

        var bytes = new byte[16];
        BitConverter.GetBytes(address.V6a).CopyTo(bytes, 0);
        BitConverter.GetBytes(address.V6b).CopyTo(bytes, 4);
        BitConverter.GetBytes(address.V6c).CopyTo(bytes, 8);
        BitConverter.GetBytes(address.V6d).CopyTo(bytes, 12);
        return new IPAddress(bytes, address.ScopeId);
    }

    // The v6 address as four uints, so the row keeps the alignment of the native one.
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct SOCKADDR_INET
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public uint V4;
        [FieldOffset(8)] public uint V6a;
        [FieldOffset(12)] public uint V6b;
        [FieldOffset(16)] public uint V6c;
        [FieldOffset(20)] public uint V6d;
        [FieldOffset(24)] public uint ScopeId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    private struct MIB_UNICASTIPADDRESS_ROW
    {
        [FieldOffset(0)] public SOCKADDR_INET Address;
        [FieldOffset(32)] public ulong InterfaceLuid;
        [FieldOffset(60)] public byte OnLinkPrefixLength;
        [FieldOffset(61)] public byte SkipAsSource;
        [FieldOffset(64)] public int DadState;
    }

    // Only the fields this sets are named; the room to spare takes a longer row from a newer Windows.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct MIB_IPINTERFACE_ROW
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(8)] public ulong InterfaceLuid;
        [FieldOffset(56)] public uint DadTransmits;
        [FieldOffset(144)] public uint SitePrefixLength;
    }

    [LibraryImport("iphlpapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ConvertInterfaceAliasToLuid(string alias, out ulong luid);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW row);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint SetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW row);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetUnicastIpAddressTable(ushort family, out IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW row);

    [LibraryImport("iphlpapi.dll")]
    private static partial void InitializeUnicastIpAddressEntry(out MIB_UNICASTIPADDRESS_ROW row);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint CreateUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW row);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint DeleteUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW row);
}
