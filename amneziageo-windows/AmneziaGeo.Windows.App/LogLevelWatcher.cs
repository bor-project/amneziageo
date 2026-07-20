using AmneziaGeo.Decl;
using Microsoft.Extensions.Hosting;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Keeps live diagnostic switches in sync with their persisted settings across both processes.
/// </summary>
internal static class LogLevelWatcher
{
    /// <summary>
    /// The settings key holding the persisted verbosity token.
    /// </summary>
    public const string SettingKey = "log-level";

    // How often the poll re-reads the setting.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Applies the persisted diagnostic settings once.
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

            // Apply the routing-log toggle on the same poll.
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

    // Accept other truthy spellings a CLI might write.
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
