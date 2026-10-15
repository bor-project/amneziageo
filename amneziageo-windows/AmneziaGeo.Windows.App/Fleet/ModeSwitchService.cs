using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// Runs the supervisor the machine is set to and changes it over when the flag moves: the set of tunnels, or
/// the single one. Each keeps a state of its own, so switching back stands the machine where it was.
/// </summary>
internal sealed class ModeSwitchService(
    IServiceProvider services,
    AgentMode mode,
    AgentControl selected,
    FleetControl fleet,
    SettingsStore settingsStore,
    ILogger<ModeSwitchService> logger) : BackgroundService
{
    // How often the flag is re-read; the setting is written by the window and by the command line alike.
    private static readonly TimeSpan _poll = TimeSpan.FromSeconds(5);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var supervisor = await RaiseAsync(mode.MultiServer, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await mode.NapAsync(_poll, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var wanted = await ReadFlagAsync(stoppingToken);
            if (wanted == mode.MultiServer)
            {
                continue;
            }

            logger.LogInformation("several servers at once was turned {State}; the machine changes over to the {Supervisor}",
                wanted ? "on" : "off", wanted ? "set of tunnels" : "single tunnel");
            if (wanted)
            {
                mode.SoleWasUp = selected.Running;
            }

            var setStands = fleet.Wanted.Count > 0;
            mode.Leaving();
            await LowerAsync(supervisor);
            var soleBack = SoleComesBack(wanted, mode.SoleWasUp, setStands);
            if (soleBack)
            {
                logger.LogInformation("the machine goes back on '{Config}', the tunnel it stood on before the set took it over", selected.Target);
            }
            else if (!wanted && mode.SoleWasUp)
            {
                logger.LogInformation("the set was down when the flag moved, so '{Config}' is not connected until you ask", selected.Target);
            }

            // Each mode answers for what is up from here on: the single tunnel is put back on what it stood on
            // before the set took the machine over, and the set stands up on its own tunnels.
            selected.SetRunning(soleBack);

            mode.MultiServer = wanted;
            mode.Switched = true;
            supervisor = await RaiseAsync(wanted, stoppingToken);
        }

        await LowerAsync(supervisor);
    }

    /// <summary>
    /// Whether the single tunnel goes back up as the flag moves: the flag goes off, the machine stood on that
    /// tunnel before the set took it over, and the set still stands.
    /// </summary>
    internal static bool SoleComesBack(bool wanted, bool soleWasUp, bool setStands)
    {
        return !wanted && soleWasUp && setStands;
    }

    private async Task<BackgroundService> RaiseAsync(bool multiServer, CancellationToken ct)
    {
        var supervisor = multiServer
            ? (BackgroundService)ActivatorUtilities.CreateInstance<FleetHostedService>(services)
            : ActivatorUtilities.CreateInstance<AgentBackgroundService>(services);
        await supervisor.StartAsync(ct);
        return supervisor;
    }

    // The tunnels come down with the supervisor; the wait is left untimed so the teardown runs to the end.
    private static async Task LowerAsync(BackgroundService supervisor)
    {
        await supervisor.StopAsync(CancellationToken.None);
        supervisor.Dispose();
    }

    private async Task<bool> ReadFlagAsync(CancellationToken ct)
    {
        try
        {
            var settings = await settingsStore.LoadAsync(ct);
            return settings.MultiServer;
        }
        catch (OperationCanceledException)
        {
            return mode.MultiServer;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "the several-servers flag could not be read; the machine stays on the supervisor it runs");
            return mode.MultiServer;
        }
    }
}
