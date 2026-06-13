using AmneziaGeo.Decl;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Loads and persists application settings as key/value rows in the state store.
/// </summary>
internal static class SettingsStore
{
    /// <summary>
    /// Loads all settings, falling back to defaults for absent or invalid values.
    /// </summary>
    public static async Task<AppSettings> LoadAsync(IStateStore store, CancellationToken ct = default)
    {
        var defaults = new AppSettings();
        return new AppSettings
        {
            RefreshSeconds = await ReadIntAsync(store, "refresh-seconds", defaults.RefreshSeconds, ct),
            ConnectTimeoutSeconds = await ReadIntAsync(store, "connect-timeout-seconds", defaults.ConnectTimeoutSeconds, ct),
            DeadThresholdSeconds = await ReadIntAsync(store, "dead-threshold-seconds", defaults.DeadThresholdSeconds, ct),
        };
    }

    /// <summary>
    /// Sets one of the known integer settings, returning false for an unknown key or non-positive value.
    /// </summary>
    public static async Task<bool> SetAsync(IStateStore store, string key, string value, CancellationToken ct = default)
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
        return ["refresh-seconds", "connect-timeout-seconds", "dead-threshold-seconds"];
    }

    private static async Task<int> ReadIntAsync(IStateStore store, string key, int fallback, CancellationToken ct)
    {
        var value = await store.GetSettingAsync(key, ct);
        return value is not null && int.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
