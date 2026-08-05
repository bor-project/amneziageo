using System.Collections.Concurrent;
using System.Net;
using AmneziaGeo.Linux.Engine;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Installs host routes for destinations as they are first used: a tunneled address is advertised to the engine
/// and pointed at the interface, a bypassed one is pinned to the physical hop. An address nothing has touched
/// for the route lifetime loses its route and is decided again on the next contact.
/// </summary>
internal sealed class LiveRoutes
{
    private const int MinSweepSeconds = 15;
    private const int MaxSweepSeconds = 120;

    private readonly string _iface;
    private readonly string? _peerKey;
    private readonly AwgDaemon _daemon;
    private readonly string? _gateway;
    private readonly string? _device;
    private readonly AgentLog _log;
    private readonly ConcurrentDictionary<string, long> _tunneled = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _bypassed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _uapiGate = new(1, 1);
    private int _ttlSeconds;

    /// <summary>
    /// ctor
    /// </summary>
    public LiveRoutes(string interfaceName, string? peerPublicKeyHex, AwgDaemon daemon, string? gateway, string? device, int ttlSeconds, AgentLog log)
    {
        _iface = interfaceName;
        _peerKey = peerPublicKeyHex;
        _daemon = daemon;
        _gateway = gateway;
        _device = device;
        _ttlSeconds = ttlSeconds;
        _log = log;
    }

    /// <summary>
    /// Addresses routed into the tunnel so far.
    /// </summary>
    public IReadOnlyCollection<string> Tunneled => _tunneled.Keys.ToList();

    /// <summary>
    /// Addresses pinned to the physical hop so far.
    /// </summary>
    public IReadOnlyCollection<string> Bypassed => _bypassed.Keys.ToList();

    /// <summary>
    /// Idle window a route survives without traffic; zero keeps only what a live socket holds.
    /// </summary>
    public int TtlSeconds => _ttlSeconds;

    /// <summary>
    /// Sets the route lifetime for the running connection.
    /// </summary>
    public void SetTtl(int seconds) => _ttlSeconds = seconds;

    /// <summary>
    /// Starts dropping routes nothing has used for the route lifetime.
    /// </summary>
    public void StartExpiry(CancellationToken ct) => _ = Task.Run(() => ExpireAsync(ct), ct);

    /// <summary>
    /// Routes an address into the tunnel, advertising it to the engine first.
    /// </summary>
    public async Task<bool> TunnelAsync(IPAddress address, string reason, CancellationToken ct)
    {
        var host = $"{address}/{Prefix(address)}";
        if (_bypassed.ContainsKey(host))
        {
            return false;
        }

        if (!_tunneled.TryAdd(host, Environment.TickCount64))
        {
            _tunneled[host] = Environment.TickCount64;
            return false;
        }

        if (_peerKey is not null)
        {
            await _uapiGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _daemon.AddAllowedIpAsync(_peerKey, host, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _tunneled.TryRemove(host, out _);
                _log.Error("route", $"advertising {host} to the engine failed", ex);
                return false;
            }
            finally
            {
                _uapiGate.Release();
            }
        }

        var added = await Shell.RunAsync("ip", ct, "route", "replace", host, "dev", _iface).ConfigureAwait(false);
        if (added.ExitCode != 0)
        {
            _tunneled.TryRemove(host, out _);
            _log.Warn("route", $"ip route replace {host} dev {_iface} failed: {added.Output}");
            return false;
        }

        _log.Route($"{host} dev {_iface} ({reason})");
        return true;
    }

    /// <summary>
    /// Pins an address to the physical hop so it stays off the tunnel.
    /// </summary>
    public async Task<bool> BypassAsync(IPAddress address, string reason, CancellationToken ct)
    {
        if (_gateway is null || _device is null)
        {
            return false;
        }

        var host = $"{address}/{Prefix(address)}";
        if (_tunneled.ContainsKey(host) || !_bypassed.TryAdd(host, 0))
        {
            return false;
        }

        var added = await Shell.RunAsync("ip", ct, "route", "replace", host, "via", _gateway, "dev", _device).ConfigureAwait(false);
        if (added.ExitCode != 0)
        {
            _bypassed.TryRemove(host, out _);
            _log.Warn("route", $"ip route replace {host} via {_gateway} failed: {added.Output}");
            return false;
        }

        _log.Route($"{host} via {_gateway} dev {_device} ({reason})");
        return true;
    }

    /// <summary>
    /// Removes the bypass routes; the tunneled ones go with the interface.
    /// </summary>
    public async Task ClearAsync(CancellationToken ct)
    {
        foreach (var host in _bypassed.Keys)
        {
            await Shell.RunAsync("ip", ct, "route", "del", host).ConfigureAwait(false);
        }

        _bypassed.Clear();
        _tunneled.Clear();
    }

    private async Task ExpireAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_ttlSeconds / 4, MinSweepSeconds, MaxSweepSeconds)), ct).ConfigureAwait(false);
                await SweepAsync(_ttlSeconds, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Drops the routes nothing has resolved or connected to within the lifetime. The range stays with the peer:
    // the route is what picks the path, and the address earns it again on the next lookup.
    private async Task SweepAsync(int ttlSeconds, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 - (ttlSeconds * 1000L);
        var idle = _tunneled.Where(entry => entry.Value < deadline).Select(entry => entry.Key).ToList();
        if (idle.Count == 0)
        {
            return;
        }

        var active = ProcNet.ActivePeers();
        foreach (var host in idle)
        {
            if (active.Contains(host[..host.IndexOf('/', StringComparison.Ordinal)]))
            {
                _tunneled[host] = Environment.TickCount64;
                continue;
            }

            if (!_tunneled.TryRemove(host, out _))
            {
                continue;
            }

            await Shell.RunAsync("ip", ct, "route", "del", host, "dev", _iface).ConfigureAwait(false);
            _log.Route($"{host} forgotten after {ttlSeconds} s unused");
        }
    }

    private static int Prefix(IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
}
