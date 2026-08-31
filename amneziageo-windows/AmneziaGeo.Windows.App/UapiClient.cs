using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Talks to the AmneziaWG device over its UAPI named pipe.
/// </summary>
internal sealed class UapiClient(ILogger<UapiClient> logger) : IDisposable
{
    // Documentation-reserved address (RFC 5737) an allowed-ips set never carries, so the probe below removes nothing.
    private const string ProbeCidr = "192.0.2.1/32";

    // Quiet window a withdrawal waits for company, and the size that sends the batch without waiting.
    private const int WithdrawWindowMs = 750;
    private const int MaxPendingWithdrawals = 512;

    // Per-prefix removal support: 0 unknown, 1 supported, 2 unsupported.
    private int _removalSupport;

    private readonly object _pendingLock = new();
    private readonly Dictionary<(string Tunnel, string Peer), HashSet<string>> _pending = [];
    private Timer? _withdrawTimer;

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

    /// <summary>
    /// Queues allowed IPs for removal. An eviction pass produces prefixes one at a time and each request is a pipe
    /// round-trip, so they leave together after a quiet window or once the batch is large enough.
    /// </summary>
    public void QueueRemoveAllowedIps(string tunnelName, string peerPublicKeyBase64, IReadOnlyList<string> cidrs)
    {
        if (cidrs.Count == 0)
        {
            return;
        }

        var full = false;
        lock (_pendingLock)
        {
            var key = (tunnelName, peerPublicKeyBase64);
            if (!_pending.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _pending[key] = set;
            }

            foreach (var cidr in cidrs)
            {
                set.Add(cidr);
            }

            full = set.Count >= MaxPendingWithdrawals;
            if (!full)
            {
                _withdrawTimer ??= new Timer(_ => FlushWithdrawals(), null, Timeout.Infinite, Timeout.Infinite);
                _withdrawTimer.Change(WithdrawWindowMs, Timeout.Infinite);
            }
        }

        if (full)
        {
            FlushWithdrawals();
        }
    }

    /// <summary>
    /// Sends every queued removal now; called on teardown so nothing outlives the tunnel it belonged to.
    /// </summary>
    public void FlushWithdrawals()
    {
        var batches = default(KeyValuePair<(string Tunnel, string Peer), HashSet<string>>[]);
        lock (_pendingLock)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batches = [.. _pending];
            _pending.Clear();
        }

        // Off-lock: the pipe round-trip must not hold the queue a caller writes into.
        foreach (var (key, set) in batches)
        {
            try
            {
                RemoveAllowedIps(key.Tunnel, key.Peer, [.. set]);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "uapi: batched allowed-ip removal for {Tunnel} failed", key.Tunnel);
            }
        }
    }

    /// <summary>
    /// Stops the withdrawal timer.
    /// </summary>
    public void Dispose()
    {
        _withdrawTimer?.Dispose();
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
            logger.LogWarning("this tunnel build cannot drop a single address from the running tunnel, so addresses that stop being needed are only cleared on reconnect");
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
    /// Binds the tunnel socket to another source port. A zero asks the device for one of its own, which is what
    /// a NAT that has forgotten the session needs: the same port keeps landing in the same discarded mapping.
    /// </summary>
    public bool Rebind(string tunnelName)
    {
        try
        {
            return Send(tunnelName, "set=1\nlisten_port=0\n\n");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "uapi: rebinding the socket of {Tunnel} failed", tunnelName);
            return false;
        }
    }

    /// <summary>
    /// Points the peer at the address given, for a server that has moved since the session was raised.
    /// </summary>
    public bool SetEndpoint(string tunnelName, string peerPublicKeyBase64, string endpoint)
    {
        try
        {
            var peerHex = Convert.ToHexStringLower(Convert.FromBase64String(peerPublicKeyBase64));
            return Send(tunnelName, $"set=1\npublic_key={peerHex}\nendpoint={endpoint}\n\n");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "uapi: pointing {Tunnel} at {Endpoint} failed", tunnelName, endpoint);
            return false;
        }
    }

    /// <summary>
    /// The address the running tunnel dials, or null when the device is unreachable or names none.
    /// </summary>
    public string? TryGetEndpoint(string tunnelName)
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

        foreach (var line in state.Split('\n'))
        {
            if (line.StartsWith("endpoint=", StringComparison.Ordinal))
            {
                return line["endpoint=".Length..].Trim();
            }
        }

        return null;
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
        var pipeName = $@"ProtectedPrefix\Administrators\AmneziaWG\{TunnelDevice.NameOf(tunnelName)}";
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
