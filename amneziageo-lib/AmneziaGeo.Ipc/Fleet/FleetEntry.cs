namespace AmneziaGeo.Ipc.Fleet;

/// <summary>
/// One server as the mode sees it: the role it holds, whether it is asked for and what it carries for the machine.
/// </summary>
/// <param name="Name">The configuration.</param>
/// <param name="Role">Its role: primary, reserve or neutral.</param>
/// <param name="Wanted">Whether the machine is asked to keep it up.</param>
/// <param name="CarriesDefault">Whether it carries what no rule sends elsewhere.</param>
/// <param name="HoldsResolver">Whether this machine's name lookups go through it.</param>
/// <param name="Slot">Its place in the chain: the first carries the machine, a further one is the reserve in
/// order, and nought is out of the chain.</param>
public sealed record FleetEntry(
    string Name,
    string Role,
    bool Wanted = false,
    bool CarriesDefault = false,
    bool HoldsResolver = false,
    int Slot = 0);
