using AmneziaGeo.Ipc.Fleet;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Answers what each tunnel is on the hook for. One tunnel on a machine holds every duty; a set of them hands
/// the duties out itself.
/// </summary>
internal class TunnelDutyRoster
{
    /// <summary>
    /// The duties of the named tunnel.
    /// </summary>
    public virtual TunnelDuties For(string name)
    {
        return TunnelDuties.Sole;
    }

    /// <summary>
    /// The tunnels a sweep must leave standing alongside the named one.
    /// </summary>
    public virtual IReadOnlyCollection<string> Standing(string name)
    {
        return [name];
    }
}
