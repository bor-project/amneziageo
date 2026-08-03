using System.Collections.Concurrent;
using System.Net;
using AmneziaGeo.Linux.Engine;

namespace AmneziaGeo.Linux.App;

/// <summary>
/// Installs host routes for destinations as they are first used: a tunneled address is advertised to the engine
/// and pointed at the interface, a bypassed one is pinned to the physical hop.
/// </summary>
internal sealed class LiveRoutes
{
    private readonly string _iface;
    private readonly string? _peerKey;
    private readonly AwgDaemon _daemon;
    private readonly string? _gateway;
    private readonly string? _device;
    private readonly AgentLog _log;
    private readonly ConcurrentDictionary<string, byte> _tunneled = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _bypassed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _uapiGate = new(1, 1);

    /// <summary>
    /// ctor
    /// </summary>
    public LiveRoutes(string interfaceName, string? peerPublicKeyHex, AwgDaemon daemon, string? gateway, string? device, AgentLog log)
    {
        _iface = interfaceName;
        _peerKey = peerPublicKeyHex;
        _daemon = daemon;
        _gateway = gateway;
        _device = device;
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
    /// Routes an address into the tunnel, advertising it to the engine first.
    /// </summary>
    public async Task<bool> TunnelAsync(IPAddress address, string reason, CancellationToken ct)
    {
        var host = $"{address}/{Prefix(address)}";
        if (_bypassed.ContainsKey(host) || !_tunneled.TryAdd(host, 0))
        {
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

    private static int Prefix(IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
}
