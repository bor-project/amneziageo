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
    public async Task<FleetState> LoadAsync(CancellationToken ct = default)
    {
        var store = scope.Store;
        var order = await store.GetSettingAsync(FleetKeys.Order, ct) ?? string.Empty;
        var roles = await store.GetSettingAsync(FleetKeys.Roles, ct) ?? string.Empty;
        var primary = (await store.GetSettingAsync(FleetKeys.Primary, ct) ?? string.Empty).Trim();
        var desired = await store.GetSettingAsync(FleetKeys.Desired, ct) ?? string.Empty;
        var targets = await store.GetSettingAsync(FleetKeys.Targets, ct) ?? string.Empty;

        _written[FleetKeys.Order] = order;
        _written[FleetKeys.Roles] = roles;
        _written[FleetKeys.Primary] = primary;
        _written[FleetKeys.Desired] = desired;
        _written[FleetKeys.Targets] = targets;

        return new FleetState(
            FleetState.ParseNames(order),
            FleetState.ParseRoles(roles),
            primary,
            FleetState.ParseNames(desired),
            FleetTargets.Parse(targets));
    }

    /// <summary>
    /// Writes what the mode stands on.
    /// </summary>
    public async Task SaveAsync(FleetState state, CancellationToken ct = default)
    {
        await WriteAsync(FleetKeys.Order, FleetState.FormatNames(state.Order), ct);
        await WriteAsync(FleetKeys.Roles, FleetState.FormatRoles(state.Roles), ct);
        await WriteAsync(FleetKeys.Primary, state.Primary, ct);
        await WriteAsync(FleetKeys.Desired, FleetState.FormatNames(state.Desired), ct);
        await WriteAsync(FleetKeys.Targets, FleetTargets.Format(state.Targets), ct);
    }

    // Writes only what moved: the set is saved on every request, and most requests move one key of the five.
    private async Task WriteAsync(string key, string value, CancellationToken ct)
    {
        if (_written.TryGetValue(key, out var last) && string.Equals(last, value, StringComparison.Ordinal))
        {
            return;
        }

        await scope.Store.SetSettingAsync(key, value, ct);
        _written[key] = value;
    }
}
