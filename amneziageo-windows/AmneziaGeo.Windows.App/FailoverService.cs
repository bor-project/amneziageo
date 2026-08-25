using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Carries the default route off a server that stopped answering and onto the next one in the priority list.
/// The decision belongs to <see cref="FailoverPolicy"/>; this reads the tunnels, hands it what it sees and does
/// what it asks for.
/// </summary>
internal sealed class FailoverService(
    AgentControl control,
    SettingsStore settingsStore,
    ScopedStoreFactory stores,
    ServiceManager serviceManager,
    AgentStatusBroker broker,
    ILogger<FailoverService> logger) : BackgroundService
{
    // Rounds are folded at the pace the tunnels are read at: nothing fresher than the liveness poll gets here.
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

    private readonly FailoverPolicy _policy = new();

    // Servers the route has been handed to since one last carried it. Without them the walk keeps to the head of
    // the list and a server further down is never dialled.
    private readonly List<string> _dialled = [];

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RoundAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One failed round is not a reason to leave the servers unwatched for the rest of the session.
                logger.LogDebug(ex, "the auto-switching round could not run");
            }
        }
    }

    // Folds one round of readings and does what the policy asks for.
    private async Task RoundAsync(CancellationToken ct)
    {
        // Auto-switching moves the default route, so it has nothing to say while no tunnel carries it.
        if (control.Find(control.DefaultRouteOwner) is not { } holder)
        {
            return;
        }

        if (holder.HandshakeAge >= 0)
        {
            // A server that answers ends the walk that was looking for one.
            _dialled.Clear();
        }
        else if (holder.RetryAttempt == 0)
        {
            // Inside its first dial: the connect budget is not spent yet, so the round says nothing either way.
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var app = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var settings = new FailoverSettings(app.FailoverEnabled, app.FailoverReturnMinutes);
        if (!settings.Enabled)
        {
            // Switched off: the policy forgets the streaks, so switching it back on starts from a clean slate.
            _policy.Decide([], settings, holder.Config, now);
            return;
        }

        var store = stores.For(holder.OwnerRoot);
        var order = await new ConfigRepository(store, serviceManager).ListAsync(ct).ConfigureAwait(false);
        var participants = FailoverPolicy.Participants(order, app.FailoverSkipped);
        var decision = _policy.Decide([.. participants.Select(Read)], settings, holder.Config, now);
        if (decision.Action == FailoverAction.Stay)
        {
            return;
        }

        var next = FailoverPolicy.Walk(participants, holder.Config, decision.Config, _dialled);
        if (next.Length == 0)
        {
            // Every participant has been dialled since the route was carried; the walk starts another lap.
            _dialled.Clear();
            next = decision.Config;
        }

        await MoveAsync(store, holder, next, decision.Action, ct).ConfigureAwait(false);
    }

    // What one server looks like from here. A tunnel whose peer has answered speaks through its counters; one
    // that is down says nothing either way.
    private FailoverReading Read(string name)
    {
        return control.Find(name) is { Running: true, HandshakeAge: >= 0 } tunnel
            ? new FailoverReading(name, true, tunnel.Link)
            : new FailoverReading(name, false, LinkReading.Empty);
    }

    // Hands the default route over: the pick is written, the server that had it goes down and the one taking its
    // place is dialled. One tunnel carries the route at a time until the hot reserve stands the next one up.
    private async Task MoveAsync(IStateStore store, TunnelControl holder, string next, FailoverAction action, CancellationToken ct)
    {
        await store.SetSettingAsync(StateKeys.DefaultRouteOwner, next, ct).ConfigureAwait(false);
        holder.SetRunning(false);
        control.For(next, holder.OwnerRoot, holder.OwnerSid).SetRunning(true);
        _dialled.Add(next);

        var desired = control.Desired.Select(tunnel => tunnel.Config);
        await store.SetSettingAsync(StateKeys.DesiredTunnels, string.Join(Environment.NewLine, desired), ct).ConfigureAwait(false);

        if (action == FailoverAction.Return)
        {
            logger.LogInformation("{Config} stands higher in the auto-switching list and answers again, so everything goes back to it while the tunnel is quiet; {Other} is taken down", next, holder.Config);
        }
        else
        {
            logger.LogWarning("{Config} stopped answering, so it is taken down and everything moves to {Other}, which is being dialled now", holder.Config, next);
        }

        await broker.BroadcastIfChangedAsync(ct).ConfigureAwait(false);
    }
}
