using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Talks to the AmneziaWG device over its UAPI named pipe.
/// </summary>
internal sealed class UapiClient(ILogger<UapiClient> logger)
{
    // Documentation-reserved address (RFC 5737) an allowed-ips set never carries, so the probe below removes nothing.
    private const string ProbeCidr = "192.0.2.1/32";

    // Per-prefix removal support: 0 unknown, 1 supported, 2 unsupported.
    private int _removalSupport;

    /// <summary>
    /// Adds an allowed IP to the peer identified by its base64 public key.
    /// </summary>
    public bool AddAllowedIp(string tunnelName, string peerPublicKeyBase64, string cidr)
    {
        return AddAllowedIps(tunnelName, peerPublicKeyBase64, [cidr]);
    }

    /// <summary>
    /// Adds allowed IPs without replacing the existing set.
    /// </summary>
    public bool AddAllowedIps(string tunnelName, string peerPublicKeyBase64, IReadOnlyList<string> cidrs)
    {
        if (cidrs.Count == 0)
        {
            return true;
        }

        return Send(tunnelName, Request(peerPublicKeyBase64, cidrs, remove: false));
    }

    /// <summary>
    /// Removes allowed IPs from the peer, leaving the rest of the set in place. Returns false when the engine has
    /// no per-prefix removal.
    /// </summary>
    public bool RemoveAllowedIps(string tunnelName, string peerPublicKeyBase64, IReadOnlyList<string> cidrs)
    {
        if (cidrs.Count == 0)
        {
            return true;
        }

        if (!SupportsRemoval(tunnelName, peerPublicKeyBase64))
        {
            return false;
        }

        return Send(tunnelName, Request(peerPublicKeyBase64, cidrs, remove: true));
    }

    // The leading minus is an AmneziaWG extension outside the documented protocol: an engine without it rejects the
    // request on the prefix instead of ignoring the line. Probed once per process against an address no set carries.
    private bool SupportsRemoval(string tunnelName, string peerPublicKeyBase64)
    {
        var known = Volatile.Read(ref _removalSupport);
        if (known != 0)
        {
            return known == 1;
        }

        var supported = Probe(tunnelName, peerPublicKeyBase64);
        Interlocked.CompareExchange(ref _removalSupport, supported ? 1 : 2, 0);
        if (!supported)
        {
            logger.LogWarning("uapi: engine has no per-prefix allowed-ip removal; stale entries stay until reconnect");
        }

        return supported;
    }

    private bool Probe(string tunnelName, string peerPublicKeyBase64)
    {
        try
        {
            return Send(tunnelName, Request(peerPublicKeyBase64, [ProbeCidr], remove: true));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "uapi: allowed-ip removal probe failed");
            return false;
        }
    }

    private static string Request(string peerPublicKeyBase64, IReadOnlyList<string> cidrs, bool remove)
    {
        var peerHex = Convert.ToHexStringLower(Convert.FromBase64String(peerPublicKeyBase64));
        var request = new StringBuilder();
        request.Append("set=1\n");
        request.Append($"public_key={peerHex}\n");
        var sign = remove ? "-" : string.Empty;
        foreach (var cidr in cidrs)
        {
            request.Append($"allowed_ip={sign}{cidr}\n");
        }

        request.Append('\n');
        return request.ToString();
    }

    private static bool Send(string tunnelName, string request)
    {
        return Exchange(tunnelName, request).Contains("errno=0", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the raw device state from a get request.
    /// </summary>
    public string Get(string tunnelName)
    {
        return Exchange(tunnelName, "get=1\n\n");
    }

    /// <summary>
    /// Returns the most recent peer handshake as unix seconds, or null when the device is unreachable.
    /// </summary>
    public long? TryGetLastHandshake(string tunnelName)
    {
        string state;
        try
        {
            state = Get(tunnelName);
        }
        catch (Exception)
        {
            return null;
        }

        long latest = 0;
        foreach (var line in state.Split('\n'))
        {
            if (line.StartsWith("last_handshake_time_sec=", StringComparison.Ordinal)
                && long.TryParse(line["last_handshake_time_sec=".Length..].Trim(), out var seconds)
                && seconds > latest)
            {
                latest = seconds;
            }
        }

        return latest;
    }

    /// <summary>
    /// Peer counters: latest handshake and summed rx/tx bytes.
    /// </summary>
    public readonly record struct PeerStatus(long HandshakeSec, long RxBytes, long TxBytes);

    /// <summary>
    /// Returns peer counters, or null when the device is unreachable.
    /// </summary>
    public PeerStatus? TryGetPeerStatus(string tunnelName)
    {
        string state;
        try
        {
            state = Get(tunnelName);
        }
        catch (Exception)
        {
            return null;
        }

        long handshake = 0;
        long rx = 0;
        long tx = 0;
        foreach (var line in state.Split('\n'))
        {
            if (line.StartsWith("last_handshake_time_sec=", StringComparison.Ordinal)
                && long.TryParse(line["last_handshake_time_sec=".Length..].Trim(), out var hs))
            {
                if (hs > handshake)
                {
                    handshake = hs;
                }
            }
            else if (line.StartsWith("rx_bytes=", StringComparison.Ordinal)
                && long.TryParse(line["rx_bytes=".Length..].Trim(), out var r))
            {
                rx += r;
            }
            else if (line.StartsWith("tx_bytes=", StringComparison.Ordinal)
                && long.TryParse(line["tx_bytes=".Length..].Trim(), out var t))
            {
                tx += t;
            }
        }

        return new PeerStatus(handshake, rx, tx);
    }

    private static string Exchange(string tunnelName, string request)
    {
        var pipeName = $@"ProtectedPrefix\Administrators\AmneziaWG\{tunnelName}";
        using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
        {
            client.Connect(5000);
            var payload = Encoding.UTF8.GetBytes(request);
            client.Write(payload, 0, payload.Length);
            client.Flush();

            var response = new StringBuilder();
            using (var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    response.AppendLine(line);
                    if (line.StartsWith("errno=", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }

            return response.ToString();
        }
    }
}
