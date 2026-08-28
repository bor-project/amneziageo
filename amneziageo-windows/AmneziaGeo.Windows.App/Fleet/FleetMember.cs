using AmneziaGeo.Ipc.Fleet;

namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// One tunnel of the set while it is up: what it was raised on the hook for, its own state and the task driving it.
/// </summary>
internal sealed class FleetMember(string name, TunnelDuties duties, long stamp, AgentControl control, CancellationTokenSource stop, Task run)
{
    /// <summary>
    /// The configuration it runs.
    /// </summary>
    public string Name => name;

    /// <summary>
    /// What it was raised on the hook for.
    /// </summary>
    public TunnelDuties Duties => duties;

    /// <summary>
    /// The rule addresses it was raised on.
    /// </summary>
    public long Stamp => stamp;

    /// <summary>
    /// Its own connection state.
    /// </summary>
    public AgentControl Control => control;

    /// <summary>
    /// Ends its supervisor.
    /// </summary>
    public CancellationTokenSource Stop => stop;

    /// <summary>
    /// The supervisor's task.
    /// </summary>
    public Task Run => run;
}
