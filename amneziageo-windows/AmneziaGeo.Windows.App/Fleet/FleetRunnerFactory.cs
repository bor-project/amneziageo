using AmneziaGeo.Ipc.Fleet;
using Microsoft.Extensions.DependencyInjection;

namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// Builds one tunnel of the set: its own state, its own owner scope and its own supervisor.
/// </summary>
internal sealed class FleetRunnerFactory(IServiceProvider services, ActiveTunnelScope owner, FleetControl fleet)
{
    /// <summary>
    /// Raises the named tunnel and returns the member driving it.
    /// </summary>
    public FleetMember Start(string name, TunnelDuties duties, CancellationToken ct)
    {
        var control = new AgentControl();
        control.SetTarget(name);
        control.SetRunning(true);

        // Every tunnel of the set belongs to the user who owns the machine's tunnels.
        var scope = ActivatorUtilities.CreateInstance<ActiveTunnelScope>(services);
        scope.SetOwner(owner.OwnerRoot, owner.OwnerSid);

        // The set hands out the duties of its own tunnels; the single-tunnel roster registered in the container
        // answers for the machine that runs one.
        var runner = ActivatorUtilities.CreateInstance<ConfigRunner>(services, control, scope, fleet);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Started off the cancellation token: a cancelled member still has to tear its tunnel down.
        return new FleetMember(name, duties, control, stop, Task.Run(() => runner.RunAsync(name, stop.Token), CancellationToken.None));
    }
}
