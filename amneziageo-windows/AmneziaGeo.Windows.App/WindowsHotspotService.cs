using System.Net;
using System.Net.NetworkInformation;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Access point of this machine: the tethering the system raises over the connection it reaches the internet by,
/// so what its clients send leaves the way this machine's own traffic does. While no point is asked for, this
/// reads the settings and asks the tethering stack one thing every few polls - whether a point stands here at
/// all - and touches nothing else.
/// </summary>
internal sealed class WindowsHotspotService(SettingsStore settings, RouteManager routes, ServiceManager services, ILogger<WindowsHotspotService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // Polls between two readings of the tethering stack while no point of ours stands.
    private const int ProbeTicks = 6;

    // Polls the sharing is given to settle on the connection asked for before the point is raised again. The
    // gateway of a tunnel swap is gone for a few seconds and back, and a point raised again on every such blink
    // drops its clients and wears the sharing stack down, so only a carrier that stays wrong this long moves it.
    private const int StaleTicks = 6;

    // Times the point is raised again over a sharing that will not move, before it is only reported.
    private const int StaleRepairs = 3;

    // Polls a sharing with no resolver of its own is given before it is put through a stop and a start.
    private const int WedgeTicks = 4;

    // Times a sharing with no resolver is put through a stop and a start, before it is only reported.
    private const int WedgeRepairs = 3;

    // Service the sharing runs as, and the port its resolver answers on.
    private const string SharingService = "SharedAccess";

    // Polls between two askings of the sharing, so a sharing that is slow to answer is not asked every tick, and
    // polls a sharing that has just been raised is given before it is asked at all.
    private const int ResolveTicks = 6;
    private const int SettleTicks = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HotspotOptions _applied = new();
    private int _probeTicks;
    private int _stale;
    private int _repairs;
    private int _wedge;
    private int _recycles;
    private int _resolveTicks;
    private bool _wasRunning;

    // Name the point of this agent carries. A point under any other name was raised by the user through Windows
    // itself and is never touched here.
    private string _raisedSsid = string.Empty;

    // Connection the point was raised over. It moves onto the gateway when that comes up and back off it when
    // it goes, and a point standing over the wrong one carries its clients out past the rules of this machine.
    private string _carrier = string.Empty;

    /// <summary>
    /// Whether the access point is up.
    /// </summary>
    public bool Running { get; private set; }

    /// <summary>
    /// Why the access point is down; empty while it holds.
    /// </summary>
    public string Error { get; private set; } = string.Empty;

    /// <summary>
    /// Whether this machine can raise an access point.
    /// </summary>
    public bool Supported { get; private set; }

    /// <summary>
    /// What stands in the way; empty while nothing does.
    /// </summary>
    public string Reason { get; private set; } = HotspotReasons.Ready;

    /// <summary>
    /// Band the access point took.
    /// </summary>
    public string BandActual { get; private set; } = string.Empty;

    /// <summary>
    /// Devices on the access point right now.
    /// </summary>
    public int Clients { get; private set; }

    /// <summary>
    /// How many devices the access point admits.
    /// </summary>
    public int MaxClients { get; private set; }

    /// <summary>
    /// Reads the settings and moves the access point to them.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wanted = (await settings.LoadAsync(ct).ConfigureAwait(false)).Hotspot();

            // Nothing asked for and no point of ours standing: Windows is reached into for the one answer the
            // window locks its switch on, and for nothing else.
            if (!wanted.Enabled && !Running)
            {
                _applied = wanted;
                Idle();
                await ProbeAsync().ConfigureAwait(false);
                return;
            }

            // Reaching into the tethering stack costs milliseconds; while nothing stands and nothing changed, it
            // is done once in a while instead of every tick.
            if (!Running && wanted == _applied && --_probeTicks > 0)
            {
                return;
            }

            _probeTicks = ProbeTicks;
            if (!Wireless())
            {
                Supported = false;
                Reason = HotspotReasons.NoAdapter;
                Idle();
                _applied = wanted;
                return;
            }

            var state = await WindowsTethering.ReadAsync(_raisedSsid.Length > 0 ? _raisedSsid : wanted.Ssid).ConfigureAwait(false);
            Take(state);
            var carrier = WindowsTethering.Carrier();
            var moved = Running && _raisedSsid.Length > 0 && !string.Equals(carrier, _carrier, StringComparison.Ordinal);
            if (moved)
            {
                logger.LogDebug("the sharing stands over '{Carrier}' while '{Wanted}' is away; a gateway that comes back takes it over without the point being touched", carrier, _carrier);
            }

            if (!Running || _raisedSsid.Length == 0 || (Carried(carrier) && !moved))
            {
                _stale = 0;
                _repairs = 0;
            }
            else
            {
                _stale++;
            }

            if (Running && !_wasRunning)
            {
                _resolveTicks = SettleTicks;
            }

            var resolves = await ResolvesAsync(ct).ConfigureAwait(false);
            _wasRunning = Running;
            if (!Running || _raisedSsid.Length == 0 || resolves is true)
            {
                _wedge = 0;
                if (resolves is true)
                {
                    _recycles = 0;
                }
            }
            else if (resolves is false)
            {
                _wedge++;
            }

            var stuck = _stale >= StaleTicks && _repairs < StaleRepairs;
            var wedged = _wedge >= WedgeTicks && _recycles < WedgeRepairs;
            if (wanted != _applied || Running != wanted.Wanted || stuck || wedged)
            {
                if (stuck)
                {
                    _stale = 0;
                    _repairs++;
                    logger.LogWarning("the sharing has stood over '{Carrier}' instead of '{Wanted}' for too long, so its clients leave past the rules of this machine; the point is raised again over the gateway", carrier, _carrier);
                }

                if (wedged)
                {
                    await RecycleAsync(ct).ConfigureAwait(false);
                }

                _applied = wanted;
                await MoveAsync(wanted, ct).ConfigureAwait(false);
                Take(await WindowsTethering.ReadAsync(_raisedSsid).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the access point could not take the settings");
            Error = ex.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await StopOnShutdownAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    // Takes the point down and puts the sharing through a stop and a start, leaving the point to be raised
    // again on the settings that follow.
    private async Task RecycleAsync(CancellationToken ct)
    {
        _wedge = 0;
        _stale = 0;
        _repairs = 0;
        _recycles++;
        logger.LogWarning("the sharing hands its clients no resolver, so the names they look up go nowhere while bare addresses still travel; the sharing is put through a stop and a start");
        await WindowsTethering.StopAsync(_raisedSsid, ct).ConfigureAwait(false);
        _raisedSsid = string.Empty;
        _carrier = string.Empty;
        if (!await Task.Run(() => services.Recycle(SharingService), ct).ConfigureAwait(false))
        {
            logger.LogWarning("the sharing did not come back up after a stop and a start");
        }
    }

    // Raises the point or takes it down, whichever the settings now ask for.
    private async Task MoveAsync(HotspotOptions options, CancellationToken ct)
    {
        if (!options.Wanted)
        {
            // Running already means a point of ours, so one the user raised through Windows stays up.
            if (Running)
            {
                await WindowsTethering.StopAsync(_raisedSsid, ct).ConfigureAwait(false);
                logger.LogInformation("the access point is down");
            }

            _raisedSsid = string.Empty;
            _carrier = string.Empty;
            Error = string.Empty;
            return;
        }

        var fault = await WindowsTethering.StartAsync(options.Ssid, options.Password, options.Band, ct).ConfigureAwait(false);
        if (fault.Length == 0)
        {
            _raisedSsid = options.Ssid;
            _carrier = WindowsTethering.Carrier();
            Error = string.Empty;
            logger.LogInformation("the access point '{Ssid}' is up, shared over '{Carrier}'", options.Ssid, _carrier);
            return;
        }

        _raisedSsid = string.Empty;
        _carrier = string.Empty;
        Error = fault;
        logger.LogWarning("the access point did not come up: {Fault}", fault);
    }

    private void Take(TetheringState state)
    {
        Supported = state.Supported;
        Reason = state.Reason;
        Running = state.Running;
        Clients = state.Clients;
        MaxClients = state.MaxClients;
        BandActual = state.Band;
    }

    private void Idle()
    {
        Running = false;
        Clients = 0;
        BandActual = string.Empty;
    }

    // Reads whether this machine raises a point at all while none is asked for, so the switch that asks for one
    // is not locked by an answer nothing ever filled in.
    private async Task ProbeAsync()
    {
        if (--_probeTicks > 0)
        {
            return;
        }

        _probeTicks = ProbeTicks;
        if (!Wireless())
        {
            Supported = false;
            Reason = HotspotReasons.NoAdapter;
            return;
        }

        var state = await WindowsTethering.ReadAsync(string.Empty).ConfigureAwait(false);
        Supported = state.Supported;
        Reason = state.Reason;
    }

    // The cheapest of the three checks, and the only one that does not reach into Windows.
    private static bool Wireless()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Any(nic => nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
    }

    // Whether the clients of the point are handed a resolver. It is told by the sharing itself: asked for a lease,
    // the sharing answers with the offer its clients get, and that offer names the resolver. Wedged, it answers
    // nothing while the point stands, and the clients keep their addresses without a resolver - bare addresses
    // still travel, names go nowhere.
    private async Task<bool?> ResolvesAsync(CancellationToken ct)
    {
        if (!Running || _raisedSsid.Length == 0)
        {
            _resolveTicks = 0;
            return true;
        }

        if (_resolveTicks > 0)
        {
            _resolveTicks--;
            return null;
        }

        _resolveTicks = ResolveTicks;
        var resolves = !IPAddress.TryParse(HotspotGateway.Scope(), out var scope)
            || await HotspotDhcpProbe.OffersResolverAsync(scope, ct).ConfigureAwait(false);
        if (!resolves)
        {
            logger.LogWarning("the sharing offers the clients of the point no resolver, so their names go nowhere while bare addresses still travel");
        }

        return resolves;
    }

    // Whether the sharing carries the point out over the connection named. Raising the point over a
    // connection is asked for and not answered: the sharing can keep the one it stood on before and say
    // nothing. What it took shows in forwarding, which it turns on for the pair it bridges and nothing
    // else on this machine does. A name no adapter carries - the Wi-Fi profile is named after its
    // network - is taken as carried, so only the gateway is ever put back.
    private bool Carried(string carrier)
    {
        var index = routes.FindInterfaceIndex(carrier);
        return index is null || routes.Forwards(index.Value);
    }

    // The point does not outlive the agent: a machine left tethering after the service stopped would carry its
    // clients out with no tunnel behind them.
    private async Task StopOnShutdownAsync()
    {
        if (!Running || _raisedSsid.Length == 0)
        {
            return;
        }

        try
        {
            await WindowsTethering.StopAsync(_raisedSsid, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the access point did not come down cleanly");
        }
        finally
        {
            Running = false;
        }
    }
}
