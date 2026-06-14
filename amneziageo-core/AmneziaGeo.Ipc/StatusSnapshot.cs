namespace AmneziaGeo.Ipc;

/// <summary>
/// A full picture of the agent's configurations and balancers and their statuses.
/// </summary>
public sealed record StatusSnapshot(
    string AgentVersion,
    string? BoundTarget,
    IReadOnlyList<ConfigEntry> Configs,
    IReadOnlyList<BalancerEntry> Balancers);
