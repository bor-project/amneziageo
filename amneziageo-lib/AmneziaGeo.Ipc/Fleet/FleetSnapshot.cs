namespace AmneziaGeo.Ipc.Fleet;

/// <summary>
/// What the mode holds: every server in the order it lists them, the one named to carry the machine and the one
/// carrying it now. Rides the status snapshot only while several tunnels may be up.
/// </summary>
/// <param name="Servers">Every server of the library, in the order the mode lists them.</param>
/// <param name="Primary">The server named to carry the machine; empty while none is.</param>
/// <param name="Carrier">The server carrying it now; empty while none does.</param>
/// <param name="Targets">Where every addressed rule rides, by the key the mode stores it under.</param>
/// <param name="Balance">How the balancer is looked at; null while the mode has not been told.</param>
public sealed record FleetSnapshot(
    IReadOnlyList<FleetEntry> Servers,
    string Primary = "",
    string Carrier = "",
    IReadOnlyDictionary<string, string>? Targets = null,
    BalancePolicy? Balance = null);
