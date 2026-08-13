using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Asks a resolver for the health name and reads whether this app's own proxy answered it. The name exists
/// nowhere else, so a foreign resolver in the proxy's place returns NXDOMAIN and reads as silence - a dead
/// receive loop, a port held by another program and an intercepted loopback all fail the same way, and each of
/// them stops every rule by domain.
/// </summary>
internal static class DnsHealthProbe
{
    private const int Port = 53;

    private const int SystemTimeoutMs = 5000;

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
}
