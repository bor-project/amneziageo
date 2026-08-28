namespace AmneziaGeo.Ipc.Fleet;

/// <summary>
/// Store keys the mode keeps its own state under. The single-tunnel keys are left alone, so each mode comes
/// back to what it last stood on.
/// </summary>
public static class FleetKeys
{
    /// <summary>
    /// The servers in the order the mode lists them, one per line.
    /// </summary>
    public const string Order = "fleet-order";

    /// <summary>
    /// The role of every server given one, one "name=role" per line.
    /// </summary>
    public const string Roles = "fleet-roles";

    /// <summary>
    /// The server that carries what no rule sends elsewhere.
    /// </summary>
    public const string Primary = "fleet-primary";

    /// <summary>
    /// The servers wanted up, one per line.
    /// </summary>
    public const string Desired = "fleet-desired";

    /// <summary>
    /// Where every addressed rule rides, one "list:token=target,fallback" per line.
    /// </summary>
    public const string Targets = "fleet-targets";
}
