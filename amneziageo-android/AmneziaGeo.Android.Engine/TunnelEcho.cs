using System.Net;
using System.Net.Sockets;
using Android.Content;
using Android.Net;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using ParcelFileDescriptor = Android.OS.ParcelFileDescriptor;

namespace AmneziaGeo.Android.Engine;

/// <summary>
/// Echoes through the tunnel of this device and nowhere else: an address is echoed only while the routes of the tunnel
/// lead into it, from a socket bound to the network the system keeps for the tunnel.
/// </summary>
internal sealed class TunnelEcho
{
    private const string Tag = "TunnelEcho";
    private const int Bound = 1;
    private const int Refused = 2;

    private readonly Context _context;
    private readonly IReadOnlyCollection<IPAddress> _hosts;
    private readonly Action<string> _report;
    private volatile Network? _network;
    private int _told;

    /// <summary>
    /// ctor
    /// </summary>
    public TunnelEcho(Context context, IReadOnlyList<string> interfaceAddresses, Action<string> report)
    {
        _context = context;
        _hosts = [.. TunnelInbound.Hosts(interfaceAddresses).Select(IPAddress.Parse)];
        _report = report;
    }

    /// <summary>
    /// Round trip in milliseconds of an echo sent through the tunnel; -1 when nothing came back or nothing was sent.
    /// </summary>
    public Task<int> RoundTripAsync(IPAddress target, int timeoutMs, CancellationToken ct)
    {
        if (Found() is not { } network || Carries(network, target) != true)
        {
            return Task.FromResult(-1);
        }

        return IcmpEcho.ConfinedAsync(target, timeoutMs, socket => Bind(network, socket), ct);
    }

    /// <summary>
    /// Whether the tunnel takes in any of the addresses; null while the system shows no network of the tunnel.
    /// </summary>
    public bool? Reaches(IReadOnlyList<IPAddress> targets)
    {
        if (Found() is not { } network)
        {
            return null;
        }

        var carried = targets.Select(target => Carries(network, target)).ToList();
        return carried.Contains(null) ? null : carried.Contains(true);
    }

    // The network the system keeps for this tunnel, looked for until it shows.
    private Network? Found()
    {
        return _network ??= AndroidNetworks.Tunnel(_context, _hosts);
    }

    // Whether the routes of the network lead the address into it; a network that is gone is forgotten.
    private bool? Carries(Network network, IPAddress target)
    {
        var carried = AndroidNetworks.Carries(_context, network, target);
        if (carried is null)
        {
            _network = null;
        }

        return carried;
    }

    // Binds the socket to the network; a network the system does not bind to is forgotten.
    private bool Bind(Network network, Socket socket)
    {
        try
        {
            var second = ParcelFileDescriptor.FromFd(socket.Handle.ToInt32());
            try
            {
                if (second?.FileDescriptor is not { } descriptor)
                {
                    return false;
                }

                network.BindSocket(descriptor);
            }
            finally
            {
                second?.Close();
            }

            if (Interlocked.Exchange(ref _told, Bound) != Bound)
            {
                _report($"the echoes of the loss probe leave on the network of the tunnel alone (network {network})");
            }

            return true;
        }
        catch (Java.Lang.Exception ex)
        {
            global::Android.Util.Log.Warn(Tag, "binding an echo to the network of the tunnel failed: " + ex);
            _network = null;
            if (Interlocked.Exchange(ref _told, Refused) != Refused)
            {
                _report("the system binds no echo to the network of the tunnel, so the loss probe sends none: " + ex.Message);
            }

            return false;
        }
    }
}
