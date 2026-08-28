using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// The names the tunnels standing alongside carry. A machine looks addresses up through one tunnel, so a name
/// matched by a rule riding another one arrives here: it is handed to the tunnel that owns it, and that one
/// resolves it and puts the addresses on its own path.
/// </summary>
internal sealed class FleetLentNames(IStateStore store, string tunnel, ILogger logger)
{
    // Answered when the owner carried nothing, so an empty reply stays what it has always been: no answer.
    private const string Nothing = "-";

    private volatile IReadOnlyList<FleetLentOwner> _owners = [];

    /// <summary>
    /// How many tunnels standing alongside carry names of their own.
    /// </summary>
    public int Count => _owners.Count;

    /// <summary>
    /// Re-reads which of them carries which names.
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        var owners = new List<FleetLentOwner>();
        foreach (var peer in TunnelPaths.Peers(await store.GetSettingAsync(TunnelPaths.PeersKey(tunnel), ct)))
        {
            if (string.Equals(peer, tunnel, StringComparison.Ordinal))
            {
                continue;
            }

            var domains = (await store.GetActiveTunnelGeoAsync(peer, ct))?.Domains ?? [];
            if (domains.Count == 0)
            {
                continue;
            }

            owners.Add(new FleetLentOwner(peer, new DomainMatcher(domains)));
            logger.LogInformation("{Peer}: {Count} name(s) are named by rules riding it, so this tunnel hands their lookups over instead of answering them", peer, domains.Count);
        }

        _owners = owners;
    }

    /// <summary>
    /// The tunnel a rule named the name on, or null.
    /// </summary>
    public string? Owner(string name)
    {
        return OwnerOf(_owners, name);
    }

    /// <summary>
    /// The tunnel out of the given ones a rule named the name on, or null.
    /// </summary>
    public static string? OwnerOf(IReadOnlyList<FleetLentOwner> owners, string name)
    {
        foreach (var owner in owners)
        {
            if (owner.Matcher.IsTunneled(name))
            {
                return owner.Tunnel;
            }
        }

        return null;
    }

    /// <summary>
    /// Has the owner look the name up and carry its addresses; empty when it carried none.
    /// </summary>
    public async Task<IReadOnlyList<string>> CarryAsync(string owner, string name)
    {
        var answer = await Task.Run(() => RuntimeSnapshotPipe.Send(owner, RuntimeSnapshotPipe.Carry(name), logger));
        if (answer is null)
        {
            Forget(owner);
            return [];
        }

        if (string.IsNullOrWhiteSpace(answer) || answer.Trim() == Nothing)
        {
            return [];
        }

        return answer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // Drops a tunnel that stopped serving its pipe, so the next name is not held up waiting for it again. It
    // comes back with the next reload, which every connect of the set brings.
    private void Forget(string owner)
    {
        var kept = _owners.Where(known => !string.Equals(known.Tunnel, owner, StringComparison.Ordinal)).ToArray();
        if (kept.Length == _owners.Count)
        {
            return;
        }

        _owners = kept;
        logger.LogWarning("{Owner} no longer answers, so the names its rules name are looked up here until it is back", owner);
    }
}

/// <summary>
/// One tunnel standing alongside and the names its own rules carry.
/// </summary>
/// <param name="Tunnel">The tunnel the names ride.</param>
/// <param name="Matcher">The names its rules matched.</param>
internal sealed record FleetLentOwner(string Tunnel, DomainMatcher Matcher);
