using AmneziaGeo.Decl;
using Microsoft.Extensions.Hosting;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Keeps the live diagnostic switches - the log level (<see cref="LogLevelController"/>) and the routing log
/// toggle (<see cref="RouteLog"/>) - in sync with their persisted settings across both processes (#82). The
/// agent runs <see cref="LogLevelBackgroundWatcher"/> as a hosted service and also pushes changes instantly
/// from the set-setting IPC handler; the per-tunnel service process, which has no host, applies them once at
/// bring-up and then polls so a support-requested change takes effect on a running tunnel without a reconnect.
/// The shared state database is the single channel between the processes.
/// </summary>
internal static class LogLevelWatcher
{
    /// <summary>
    /// The settings key holding the persisted verbosity token.
    /// </summary>
    public const string SettingKey = "log-level";

    // How often the poll re-reads the setting. Cheap (a single key lookup) and matched to how promptly a
    // support engineer expects "switch to trace" to take hold on an already-connected tunnel.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reads the persisted diagnostic settings once and applies them: the log level (a missing or invalid
    /// value leaves the current level untouched, Information by default) and the routing-log on/off toggle. A
    /// transient read failure is swallowed so it never crashes the caller.
    /// </summary>
    public static async Task ApplyAsync(IStateStore store, LogLevelController controller, CancellationToken ct = default)
    {
        try
        {
            var token = await store.GetSettingAsync(SettingKey, ct);
            if (LogLevelController.IsValid(token))
            {
                controller.Set(token);
            }

            // The routing log is an independent toggle applied on the same poll, so "включите лог
            // маршрутизации" from support takes effect live in both the agent and the tunnel process.
            var routeLog = await store.GetSettingAsync(RouteLog.SettingKey, ct);
            RouteLog.Enabled = IsTrue(routeLog);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A transient DB read must not stop logging or the poll loop.
        }
    }

    // The routing-log setting is persisted by SettingsStore as the canonical "true"/"false", but accept the
    // other truthy spellings a CLI set-option might write so an out-of-band value still enables the log.
    private static bool IsTrue(string? value)
    {
        return value?.Trim().ToLowerInvariant() is "true" or "on" or "1" or "yes";
    }

    /// <summary>
    /// Applies the level immediately, then re-applies on each poll until cancelled.
    /// </summary>
    public static async Task RunAsync(IStateStore store, LogLevelController controller, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await ApplyAsync(store, controller, ct);
            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>
/// Hosted wrapper that runs the log-level poll for the agent process.
/// </summary>
internal sealed class LogLevelBackgroundWatcher(IStateStore store, LogLevelController controller) : BackgroundService
{
    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return LogLevelWatcher.RunAsync(store, controller, stoppingToken);
    }
}
