using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

/// <summary>
/// The routing lists a configuration whose server allows none but its own leaves to choose from.
/// </summary>
/// <param name="Config">The configuration whose server allows none but its own lists.</param>
/// <param name="Lists">The lists of that server the device holds, in the order the server hands them out.</param>
/// <param name="Pick">The list put in use in place of another one: the one the server marks default, else the first.</param>
public sealed record ListsLimit(string Config, IReadOnlyList<long> Lists, long Pick)
{
    /// <summary>
    /// Whether a list may be in use; none may not.
    /// </summary>
    public bool Allows(long? listId) => listId is { } id && Lists.Contains(id);
}

/// <summary>
/// Keeps the routing list in use among the lists of a server that allows none but its own.
/// </summary>
public static class ServerListsOnly
{
    /// <summary>
    /// Returns the limit the server of a stored config sets, null when it allows any list or the device holds none of
    /// its lists.
    /// </summary>
    public static async Task<ListsLimit?> OfAsync(IStateStore store, string config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        return await OfAsync(store, config, await store.GetConfigTextAsync(config, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the limit the server of a config with its text sets, null when it allows any list or the device holds
    /// none of its lists.
    /// </summary>
    public static async Task<ListsLimit?> OfAsync(IStateStore store, string config, string? text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);

        var offer = await ServerOfferStore.ReadAsync(store, config, text, ct).ConfigureAwait(false);
        if (!offer.PresetsOnly)
        {
            return null;
        }

        var origins = await OriginsAsync(store, ct).ConfigureAwait(false);
        var lists = new List<long>();
        var pick = default(long?);
        foreach (var preset in offer.Presets().Where(one => one.Id.Length > 0))
        {
            if (!origins.TryGetValue(preset.Id, out var listId) || lists.Contains(listId))
            {
                continue;
            }

            lists.Add(listId);
            if (preset.Default)
            {
                pick ??= listId;
            }
        }

        return lists.Count == 0 ? null : new ListsLimit(config, lists, pick ?? lists[0]);
    }

    /// <summary>
    /// Returns the limit the first of the configs whose server allows none but its own lists sets, null when none
    /// does.
    /// </summary>
    public static async Task<ListsLimit?> FirstAsync(IStateStore store, IEnumerable<string> configs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configs);

        foreach (var config in configs)
        {
            if (await OfAsync(store, config, ct).ConfigureAwait(false) is { } limit)
            {
                return limit;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the list to put in use in place of the one in use, null when the one in use stays.
    /// </summary>
    public static long? Switch(ListsLimit? limit, long? inUse) =>
        limit is null || limit.Allows(inUse) ? null : limit.Pick;

    /// <summary>
    /// Returns the refusal of a command that puts in use a list the limit does not allow, or none, null when the
    /// limit allows it.
    /// </summary>
    public static IpcAck? Refusal(ListsLimit? limit, long? listId) =>
        limit is null || limit.Allows(listId) ? null : new IpcAck(false, IpcMessage.Key("Agent_RoutingListsServerOnly", limit.Config));

    /// <summary>
    /// Returns the refusal of a command that keeps a stored config off the routing list where its server allows none
    /// but its own lists, null when it may.
    /// </summary>
    public static async Task<IpcAck?> RoutingOffRefusalAsync(IStateStore store, string config, bool useRouting, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        return await RoutingOffRefusalAsync(store, config, await store.GetConfigTextAsync(config, ct).ConfigureAwait(false), useRouting, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the refusal of a command that keeps a config with its text off the routing list where its server allows
    /// none but its own lists, null when it may.
    /// </summary>
    public static async Task<IpcAck?> RoutingOffRefusalAsync(IStateStore store, string config, string? text, bool useRouting, CancellationToken ct) =>
        !useRouting && (await ServerOfferStore.ReadAsync(store, config, text, ct).ConfigureAwait(false)).PresetsOnly
            ? new IpcAck(false, IpcMessage.Key("Agent_RoutingListsServerOnly", config))
            : null;

    /// <summary>
    /// Turns the routing list on for a stored config whose server allows none but its own lists; returns whether it
    /// was off.
    /// </summary>
    public static async Task<bool> RoutingOnAsync(IStateStore store, string config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);

        return await RoutingOnAsync(store, config, await store.GetConfigTextAsync(config, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns the routing list on for a config with its text whose server allows none but its own lists; returns
    /// whether it was off.
    /// </summary>
    public static async Task<bool> RoutingOnAsync(IStateStore store, string config, string? text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(config);

        if (await store.GetConfigTransportAsync(config, ct).ConfigureAwait(false) is not { UseRouting: false } transport
            || !(await ServerOfferStore.ReadAsync(store, config, text, ct).ConfigureAwait(false)).PresetsOnly)
        {
            return false;
        }

        await store.SetConfigTransportAsync(transport with { UseRouting = true }, ct).ConfigureAwait(false);

        return true;
    }

    // The list each identifier of a server stands for.
    private static async Task<Dictionary<string, long>> OriginsAsync(IStateStore store, CancellationToken ct)
    {
        var origins = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var origin in await store.ListRoutingListOriginsAsync(ct).ConfigureAwait(false))
        {
            origins.TryAdd(origin.PresetId, origin.ListId);
        }

        return origins;
    }
}
