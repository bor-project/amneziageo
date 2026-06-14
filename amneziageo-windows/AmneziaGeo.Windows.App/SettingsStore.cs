using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Loads and persists application settings as key/value rows in the state store.
/// </summary>
internal sealed class SettingsStore(IStateStore store)
{
    /// <summary>
    /// Loads all settings, falling back to defaults for absent or invalid values.
    /// </summary>
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        var defaults = new AppSettings();
        return new AppSettings
        {
            RefreshSeconds = await ReadIntAsync("refresh-seconds", defaults.RefreshSeconds, ct),
            ConnectTimeoutSeconds = await ReadIntAsync("connect-timeout-seconds", defaults.ConnectTimeoutSeconds, ct),
            DeadThresholdSeconds = await ReadIntAsync("dead-threshold-seconds", defaults.DeadThresholdSeconds, ct),
            FailbackProbes = await ReadIntAsync("failback-probes", defaults.FailbackProbes, ct),
            ProbeTimeoutSeconds = await ReadIntAsync("probe-timeout-seconds", defaults.ProbeTimeoutSeconds, ct),
        };
    }

    /// <summary>
    /// Sets one of the known integer settings, returning false for an unknown key or non-positive value.
    /// </summary>
    public async Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
        if (!Keys().Contains(key) || !int.TryParse(value, out var parsed) || parsed <= 0)
        {
            return false;
        }

        await store.SetSettingAsync(key, parsed.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        return true;
    }

    /// <summary>
    /// Returns the known setting keys.
    /// </summary>
    public static IReadOnlyList<string> Keys()
    {
        return ["refresh-seconds", "connect-timeout-seconds", "dead-threshold-seconds", "failback-probes", "probe-timeout-seconds"];
    }

    private async Task<int> ReadIntAsync(string key, int fallback, CancellationToken ct)
    {
        var value = await store.GetSettingAsync(key, ct);
        return value is not null && int.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
