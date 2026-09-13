using AmneziaGeo.Decl;
using AmneziaGeo.Ipc.Fleet;

namespace AmneziaGeo.Windows.App.Fleet;

/// <summary>
/// Keeps the mode's own state in the owner's library. The single-tunnel keys are neither read nor written, so
/// each mode stands back up on what it left.
/// </summary>
internal sealed class FleetStore(ActiveTunnelScope scope)
{
    private readonly Dictionary<string, string> _written = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads what the mode last stood on.
    /// </summary>
    public Task<FleetState> LoadAsync(CancellationToken ct = default)
    {
        return ReadAsync(scope.Store, _written, ct);
    }

    /// <summary>
    /// Writes what the mode stands on.
    /// </summary>
    public Task SaveAsync(FleetState state, CancellationToken ct = default)
    {
        return WriteAsync(scope.Store, state, _written, ct);
    }

    /// <summary>
    /// Reads what the mode last stood on in a library and notes every key as it was read.
    /// </summary>
    public static async Task<FleetState> ReadAsync(IStateStore store, IDictionary<string, string> written, CancellationToken ct = default)
    {
        var order = await store.GetSettingAsync(FleetKeys.Order, ct).ConfigureAwait(false) ?? string.Empty;
        var roles = await store.GetSettingAsync(FleetKeys.Roles, ct).ConfigureAwait(false) ?? string.Empty;
        var primary = (await store.GetSettingAsync(FleetKeys.Primary, ct).ConfigureAwait(false) ?? string.Empty).Trim();
        var desired = await store.GetSettingAsync(FleetKeys.Desired, ct).ConfigureAwait(false) ?? string.Empty;
        var targets = await store.GetSettingAsync(FleetKeys.Targets, ct).ConfigureAwait(false) ?? string.Empty;
        var resume = await store.GetSettingAsync(FleetKeys.Resume, ct).ConfigureAwait(false) ?? string.Empty;

        written[FleetKeys.Order] = order;
        written[FleetKeys.Roles] = roles;
        written[FleetKeys.Primary] = primary;
        written[FleetKeys.Desired] = desired;
        written[FleetKeys.Targets] = targets;
        written[FleetKeys.Resume] = resume;

        return new FleetState(
            FleetState.ParseNames(order),
            FleetState.ParseRoles(roles),
            primary,
            FleetState.ParseNames(desired),
            FleetTargets.Parse(targets),
            FleetState.ParseNames(resume));
    }

    /// <summary>
    /// Writes what the mode stands on in a library, past the keys that read as noted.
    /// </summary>
    public static async Task WriteAsync(IStateStore store, FleetState state, IDictionary<string, string> written, CancellationToken ct = default)
    {
        await WriteKeyAsync(store, written, FleetKeys.Order, FleetState.FormatNames(state.Order), ct).ConfigureAwait(false);
        await WriteKeyAsync(store, written, FleetKeys.Roles, FleetState.FormatRoles(state.Roles), ct).ConfigureAwait(false);
        await WriteKeyAsync(store, written, FleetKeys.Primary, state.Primary, ct).ConfigureAwait(false);
        await WriteKeyAsync(store, written, FleetKeys.Desired, FleetState.FormatNames(state.Desired), ct).ConfigureAwait(false);
        await WriteKeyAsync(store, written, FleetKeys.Targets, FleetTargets.Format(state.Targets), ct).ConfigureAwait(false);
        await WriteKeyAsync(store, written, FleetKeys.Resume, FleetState.FormatNames(state.Resume ?? []), ct).ConfigureAwait(false);
    }

    // Writes only what moved: the set is saved on every request, and most requests move one key of the six.
    private static async Task WriteKeyAsync(IStateStore store, IDictionary<string, string> written, string key, string value, CancellationToken ct)
    {
        if (written.TryGetValue(key, out var last) && string.Equals(last, value, StringComparison.Ordinal))
        {
            return;
        }

        await store.SetSettingAsync(key, value, ct).ConfigureAwait(false);
        written[key] = value;
    }
}
