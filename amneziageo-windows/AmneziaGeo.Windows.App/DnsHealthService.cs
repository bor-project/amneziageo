using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Watches whether this machine resolves names through the running tunnel's own proxy and hands the verdict to
/// the snapshot. Its silence is invisible otherwise: rules by domain simply stop applying, so those sites either
/// leave outside the tunnel or are cut off by the leak protection, while the connection looks healthy.
/// </summary>
internal sealed class DnsHealthService(
    AgentControl control,
    AgentStatusBroker broker,
    ILogger<DnsHealthService> logger) : BackgroundService
{
    private static readonly TimeSpan _initialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(20);
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
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Only the stop token ends this watch: an inner cancellation is one failed check, not a reason
                // to leave the machine unwatched for the rest of the session.
                logger.LogDebug(ex, "the resolver check could not run");
            }

            try
            {
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        if (!control.Running)
        {
            _misses = 0;
            await ReportAsync(false, ct).ConfigureAwait(false);
            return;
        }

        // Asked the way an application asks, in the agent itself: the resolver a machine uses is chosen per
        // machine, so this answer is the one every program on it gets.
        var served = await DnsHealthProbe.SystemAnswersAsync(ct).ConfigureAwait(false);
        logger.LogDebug("resolver check: names resolve through the tunnel: {Served}, misses {Misses}", served, _misses);
        if (served)
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
            logger.LogWarning("names on this machine are not resolved through {Tunnel}: rules by domain no longer apply, so those sites either leave outside the tunnel or are cut off by the leak protection", control.RunningTarget ?? control.Target);
        }

        await ReportAsync(true, ct).ConfigureAwait(false);
    }

    // The resolver a machine uses is chosen per machine, so the verdict lands on every tunnel that is up.
    private async Task ReportAsync(bool unreachable, CancellationToken ct)
    {
        var moved = false;
        foreach (var tunnel in control.Desired)
        {
            moved |= tunnel.SetDnsUnreachable(unreachable);
        }

        if (moved)
        {
            await broker.BroadcastIfChangedAsync(ct).ConfigureAwait(false);
        }
    }
}
