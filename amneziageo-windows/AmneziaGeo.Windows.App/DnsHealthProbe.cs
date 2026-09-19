using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Asks a resolver for the health name and reads whether this app's own proxy answered it. The name exists
/// nowhere else, so a foreign resolver in the proxy's place returns NXDOMAIN and reads as silence - a dead
/// receive loop, a port held by another program and an intercepted loopback all fail the same way, and each of
/// them stops every rule by domain.
/// </summary>
internal static partial class DnsHealthProbe
{
    private const int Port = 53;

    private const int SystemTimeoutMs = 5000;

    private const uint DnsQueryRequestVersion1 = 1;
    private const ushort DnsTypeA = 1;
    // Skips the cache, local names, the hosts file, NetBIOS and multicast.
    private const ulong ServiceQueryOptions = 0x8 | 0x20 | 0x40 | 0x80 | 0x800;
    private const int DnsFreeRecordList = 1;
    private const short AfInet = 2;
    private const short AfInet6 = 23;
    // DNS_ADDR_ARRAY header, then one DNS_ADDR whose first bytes hold the sockaddr.
    private const int AddrOffset = 32;
    private const int ServerListSize = AddrOffset + 64;

    private static readonly IPAddress _health = IPAddress.Parse(DnsProxy.HealthAddress);
    private static readonly byte[] _marker = _health.GetAddressBytes();

    /// <summary>
    /// Whether the machine's own resolution path ends at our proxy. This asks the way every application asks, so
    /// it also catches the case no socket test can see: the proxy serving perfectly while the system prefers
    /// another adapter's resolvers and never sends it a thing.
    /// </summary>
    public static async Task<bool> SystemAnswersAsync(CancellationToken ct)
    {
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(SystemTimeoutMs);
            var answers = await Dns.GetHostAddressesAsync(DnsProxy.HealthName, AddressFamily.InterNetwork, attempt.Token).ConfigureAwait(false);
            foreach (var answer in answers)
            {
                if (answer.Equals(_health))
                {
                    return true;
                }
            }

            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this resolver answers the health name as our proxy does.
    /// </summary>
    public static async Task<bool> AnswersAsync(IPAddress resolver, int timeoutMs, CancellationToken ct)
    {
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        try
        {
            using var client = new UdpClient(resolver.AddressFamily);
            client.Connect(new IPEndPoint(resolver, Port));
            await client.SendAsync(Query(id).AsMemory(), ct).ConfigureAwait(false);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(timeoutMs);
            var answer = await client.ReceiveAsync(attempt.Token).ConfigureAwait(false);
            return IsOurs(answer.Buffer, id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this resolver answers the health name as our proxy does when the system's DNS client service sends
    /// the query.
    /// </summary>
    public static async Task<bool> ServiceAnswersAsync(IPAddress resolver, int timeoutMs, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => QueryThroughService(resolver))
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Asks the resolver for the health name through the DNS client service.
    private static bool QueryThroughService(IPAddress resolver)
    {
        var servers = ServerList(resolver);
        var name = Marshal.StringToHGlobalUni(DnsProxy.HealthName);
        try
        {
            var request = new DNS_QUERY_REQUEST
            {
                Version = DnsQueryRequestVersion1,
                QueryName = name,
                QueryType = DnsTypeA,
                QueryOptions = ServiceQueryOptions,
                DnsServerList = servers,
            };
            var result = new DNS_QUERY_RESULT { Version = DnsQueryRequestVersion1 };
            var status = DnsQueryEx(ref request, ref result, IntPtr.Zero);
            try
            {
                return status == 0 && HasHealthRecord(result.QueryRecords);
            }
            finally
            {
                if (result.QueryRecords != IntPtr.Zero)
                {
                    DnsFree(result.QueryRecords, DnsFreeRecordList);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(servers);
        }
    }

    // A DNS_ADDR_ARRAY holding the one resolver with a zero port.
    private static IntPtr ServerList(IPAddress resolver)
    {
        var list = Marshal.AllocHGlobal(ServerListSize);
        Marshal.Copy(new byte[ServerListSize], 0, list, ServerListSize);
        var family = resolver.AddressFamily == AddressFamily.InterNetworkV6 ? AfInet6 : AfInet;
        Marshal.WriteInt32(list, 0, 1);
        Marshal.WriteInt32(list, 4, 1);
        Marshal.WriteInt16(list, 12, family);
        Marshal.WriteInt16(list, AddrOffset, family);
        var address = resolver.GetAddressBytes();
        Marshal.Copy(address, 0, list + AddrOffset + (family == AfInet6 ? 8 : 4), address.Length);
        return list;
    }

    // Whether the records carry the address only our proxy returns for the health name.
    private static bool HasHealthRecord(IntPtr records)
    {
        for (var record = records; record != IntPtr.Zero; record = Marshal.ReadIntPtr(record))
        {
            if ((ushort)Marshal.ReadInt16(record, 2 * IntPtr.Size) != DnsTypeA)
            {
                continue;
            }

            var address = new byte[4];
            Marshal.Copy(record + (2 * IntPtr.Size) + 16, address, 0, address.Length);
            if (_health.Equals(new IPAddress(address)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Minimal A query for the health name, carrying the id its answer must echo.
    /// </summary>
    public static byte[] Query(ushort id)
    {
        var message = new List<byte>
        {
            (byte)(id >> 8), (byte)id, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

        foreach (var label in DnsProxy.HealthName.Split('.'))
        {
            message.Add((byte)label.Length);
            message.AddRange(Encoding.ASCII.GetBytes(label));
        }

        message.Add(0);
        message.AddRange([0x00, 0x01, 0x00, 0x01]);
        return [.. message];
    }

    /// <summary>
    /// Whether an answer carries this probe's id and the address only our proxy returns for the health name.
    /// </summary>
    public static bool IsOurs(byte[] answer, ushort id)
    {
        if (answer.Length < 12 + _marker.Length || answer[0] != (byte)(id >> 8) || answer[1] != (byte)id)
        {
            return false;
        }

        // NOERROR with at least one record; the marker sits in the last record's rdata.
        if ((answer[3] & 0x0F) != 0 || (answer[6] << 8 | answer[7]) == 0)
        {
            return false;
        }

        for (var i = 0; i < _marker.Length; i++)
        {
            if (answer[answer.Length - _marker.Length + i] != _marker[i])
            {
                return false;
            }
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_QUERY_REQUEST
    {
        public uint Version;
        public IntPtr QueryName;
        public ushort QueryType;
        public ulong QueryOptions;
        public IntPtr DnsServerList;
        public uint InterfaceIndex;
        public IntPtr QueryCompletionCallback;
        public IntPtr QueryContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_QUERY_RESULT
    {
        public uint Version;
        public int QueryStatus;
        public ulong QueryOptions;
        public IntPtr QueryRecords;
        public IntPtr Reserved;
    }

    [LibraryImport("dnsapi.dll")]
    private static partial int DnsQueryEx(ref DNS_QUERY_REQUEST request, ref DNS_QUERY_RESULT result, IntPtr cancel);

    [LibraryImport("dnsapi.dll")]
    private static partial void DnsFree(IntPtr data, int freeType);
}
