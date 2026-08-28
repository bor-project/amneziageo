using AmneziaGeo.Decl;
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

    /// <summary>
    /// The rules of a list the named tunnel carries. The only tunnel on a machine carries every one of them,
    /// and answering with the list it was given says exactly that.
    /// </summary>
    public virtual IReadOnlyList<GeoRule> Share(string name, long listId, IReadOnlyList<GeoRule> rules)
    {
        return rules;
    }
}
