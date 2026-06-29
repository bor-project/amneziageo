using System.IO.Pipes;
using System.Text;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Talks to the AmneziaWG device over its UAPI named pipe.
/// </summary>
internal sealed class UapiClient
{
    /// <summary>
    /// Adds an allowed IP to the peer identified by its base64 public key.
    /// </summary>
    public bool AddAllowedIp(string tunnelName, string peerPublicKeyBase64, string cidr)
    {
        var peerHex = Convert.ToHexStringLower(Convert.FromBase64String(peerPublicKeyBase64));
        var request = $"set=1\npublic_key={peerHex}\nallowed_ip={cidr}\n\n";
        return Exchange(tunnelName, request).Contains("errno=0", StringComparison.Ordinal);
    }

    /// <summary>
    /// Adds several allowed IPs to the peer in one UAPI exchange without clearing the existing set (no
    /// replace_allowed_ips). Cost is O(new) rather than the O(total) of a full <see cref="SetAllowedIps"/>
    /// replace, so the live DNS path can advertise a freshly resolved domain's IPs without re-pushing the
    /// entire multi-thousand-entry set on every resolution. Empty input is a no-op.
    /// </summary>
    public bool AddAllowedIps(string tunnelName, string peerPublicKeyBase64, IReadOnlyList<string> cidrs)
    {
        if (cidrs.Count == 0)
        {
            return true;
        }

        var peerHex = Convert.ToHexStringLower(Convert.FromBase64String(peerPublicKeyBase64));
        var request = new StringBuilder();
        request.Append("set=1\n");
        request.Append($"public_key={peerHex}\n");
        foreach (var cidr in cidrs)
        {
            request.Append($"allowed_ip={cidr}\n");
        }

        request.Append('\n');
        return Exchange(tunnelName, request.ToString()).Contains("errno=0", StringComparison.Ordinal);
    }

    /// <summary>
    /// Replaces the peer's allowed IPs with exactly the given set.
    /// </summary>
    public bool SetAllowedIps(string tunnelName, string peerPublicKeyBase64, IReadOnlyList<string> cidrs)
    {
        var peerHex = Convert.ToHexStringLower(Convert.FromBase64String(peerPublicKeyBase64));
        var request = new StringBuilder();
        request.Append("set=1\n");
        request.Append($"public_key={peerHex}\n");
        request.Append("replace_allowed_ips=true\n");
        foreach (var cidr in cidrs)
        {
            request.Append($"allowed_ip={cidr}\n");
        }

        request.Append('\n');
        return Exchange(tunnelName, request.ToString()).Contains("errno=0", StringComparison.Ordinal);
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
    /// Aggregate peer counters read from the device: the latest handshake (unix seconds, 0 = never), and
    /// the summed rx / tx bytes across peers.
    /// </summary>
    public readonly record struct PeerStatus(long HandshakeSec, long RxBytes, long TxBytes);

    /// <summary>
    /// Returns the device's peer counters, or null when the device is unreachable. This is the structured,
    /// data form of the engine's connection progress: a completed handshake (HandshakeSec &gt; 0) means
    /// connected; a server that never answers shows HandshakeSec == 0 and RxBytes == 0 even as we keep
    /// sending initiations (TxBytes grows) - the data equivalent of the engine's "handshake did not
    /// complete" log, used to detect a failed connect without scraping logs.
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
