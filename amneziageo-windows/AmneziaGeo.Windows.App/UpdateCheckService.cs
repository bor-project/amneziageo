using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Periodically checks the update URL for a new version.
/// </summary>
internal sealed class UpdateCheckService(
    SettingsStore settingsStore,
    UpdateChecker checker,
    UpdateState state,
    AgentStatusBroker broker,
    ILogger<UpdateCheckService> logger) : BackgroundService
{
    private static readonly TimeSpan _initialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_initialDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "update check failed");
            }

            try
            {
                await Task.Delay(_interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        var settings = await settingsStore.LoadAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.UpdateUrl))
        {
            return;
        }

        state.Latest = await checker.CheckAsync(
            settings.UpdateUrl, CurrentVersion, AppSettings.BuildTarget, settings.AllowPrerelease, ct);
        await broker.BroadcastIfChangedAsync(ct);
    }

    private static string CurrentVersion =>
        typeof(UpdateCheckService).Assembly.GetName().Version?.ToString() ?? "0";
}
