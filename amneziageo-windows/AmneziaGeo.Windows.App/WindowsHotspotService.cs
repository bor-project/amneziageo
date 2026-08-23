using System.Net.NetworkInformation;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Access point of this machine: the tethering the system raises over the connection it reaches the internet by,
/// so what its clients send leaves the way this machine's own traffic does. While no point is asked for, this
/// reads the settings and stops there - the adapters and the tethering stack are not touched at all.
/// </summary>
internal sealed class WindowsHotspotService(SettingsStore settings, ILogger<WindowsHotspotService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // Polls between two readings of the tethering stack while no point of ours stands.
    private const int ProbeTicks = 6;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HotspotOptions _applied = new();
    private int _probeTicks;

    // Name the point of this agent carries. A point under any other name was raised by the user through Windows
    // itself and is never touched here.
    private string _raisedSsid = string.Empty;

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

            // Nothing asked for and no point of ours standing: nothing of Windows is reached into, so a machine
            // that never shares pays a settings read and no more.
            if (!wanted.Enabled && !Running)
            {
                _applied = wanted;
                Idle();
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
            if (wanted != _applied || Running != wanted.Wanted)
            {
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
            Error = string.Empty;
            return;
        }

        var fault = await WindowsTethering.StartAsync(options.Ssid, options.Password, options.Band, ct).ConfigureAwait(false);
        if (fault.Length == 0)
        {
            _raisedSsid = options.Ssid;
            Error = string.Empty;
            logger.LogInformation("the access point '{Ssid}' is up", options.Ssid);
            return;
        }

        _raisedSsid = string.Empty;
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

    // The cheapest of the three checks, and the only one that does not reach into Windows.
    private static bool Wireless()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Any(nic => nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
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
