namespace AmneziaGeo.Ipc.Fleet;

/// <summary>
/// Requests only the mode answers. An agent with the flag off knows none of them and answers as it does any
/// other request it has no handler for.
/// </summary>
public static class FleetOps
{
    /// <summary>
    /// Command to ask for one server. Args: name, optionally "takeover" to take the machine's tunnels from
    /// another user.
    /// </summary>
    public const string Connect = "fleet-connect";

    /// <summary>
    /// Command to take one server out of the set; the rest stand. Args: name.
    /// </summary>
    public const string Disconnect = "fleet-disconnect";

    /// <summary>
    /// Command to name the server that carries what no rule sends elsewhere. Args: name.
    /// </summary>
    public const string SetPrimary = "fleet-set-primary";

    /// <summary>
    /// Command to give a server its role. Args: name, role (primary / reserve / neutral).
    /// </summary>
    public const string SetRole = "fleet-set-role";

    /// <summary>
    /// Command to list the servers in the order the mode keeps them, which is the order it falls back through.
    /// Args: the names, in that order.
    /// </summary>
    public const string Reorder = "fleet-reorder";
}
