using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Watches whether names still resolve through the running tunnel's own proxy and hands the verdict to the
/// snapshot. Its silence is invisible otherwise: rules by domain simply stop applying, every name resolves
/// outside the tunnel, and the connection goes on looking healthy.
/// </summary>
internal sealed class DnsHealthService(
    AgentControl control,
    AgentStatusBroker broker,
    ILogger<DnsHealthService> logger) : BackgroundService
{
    private static readonly TimeSpan _initialDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);
    // Silent answers in a row before the verdict turns; one lost datagram is not a broken resolver.
    private const int Strikes = 2;

    private int _misses;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_initialDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(ct).ConfigureAwait(false);
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "the resolver check could not run");
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        var tunnel = control.RunningTarget;
        if (!control.Running || string.IsNullOrEmpty(tunnel))
        {
            _misses = 0;
            await ReportAsync(false, ct).ConfigureAwait(false);
            return;
        }

        // The tunnel runs in its own process and owns both the proxy and the redirect, so the verdict is its to
        // give. A quiet logger: an unreachable pipe is a tunnel in transition, not news.
        var verdict = await Task.Run(
            () => RuntimeSnapshotPipe.Send(tunnel, RuntimeSnapshotPipe.OpDns, NullLogger.Instance), ct).ConfigureAwait(false);
        if (verdict is null)
        {
            return;
        }

        if (verdict.Trim() != RuntimeSnapshotPipe.DnsUnrouted)
        {
            if (control.DnsUnreachable)
            {
                logger.LogInformation("names are resolved through the tunnel again, so rules by domain apply once more");
            }

            _misses = 0;
            await ReportAsync(false, ct).ConfigureAwait(false);
            return;
        }

        _misses++;
        if (_misses < Strikes)
        {
            return;
        }

        if (!control.DnsUnreachable)
        {
            logger.LogWarning("names on this machine are resolved by another program, not by {Tunnel}: rules by domain no longer apply and those names leave outside the tunnel", tunnel);
        }

        await ReportAsync(true, ct).ConfigureAwait(false);
    }

    private async Task ReportAsync(bool unreachable, CancellationToken ct)
    {
        if (control.SetDnsUnreachable(unreachable))
        {
            await broker.BroadcastIfChangedAsync(ct).ConfigureAwait(false);
        }
    }
}
