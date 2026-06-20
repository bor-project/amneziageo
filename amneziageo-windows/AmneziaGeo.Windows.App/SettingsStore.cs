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
            KillSwitchEnabled = await ReadBoolAsync("killswitch", defaults.KillSwitchEnabled, ct),
            AllowLan = await ReadBoolAsync("allow-lan", defaults.AllowLan, ct),
            UpdateUrl = await ReadStringAsync("update-url", defaults.UpdateUrl, ct),
            GeoAutoCheck = await ReadBoolAsync("geo-auto-check", defaults.GeoAutoCheck, ct),
            GeoCheckIntervalHours = await ReadIntAsync("geo-check-interval-hours", defaults.GeoCheckIntervalHours, ct),
        };
    }

    /// <summary>
    /// Sets a known setting, returning false for an unknown key or an invalid value. Integer settings
    /// must be positive; boolean settings accept true/false/on/off/1/0/yes/no.
    /// </summary>
    public async Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
        if (IntKeys.Contains(key))
        {
            if (!int.TryParse(value, out var parsed) || parsed <= 0)
            {
                return false;
            }

            await store.SetSettingAsync(key, parsed.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
            return true;
        }

        if (BoolKeys.Contains(key))
        {
            if (!TryParseBool(value, out var flag))
            {
                return false;
            }

            await store.SetSettingAsync(key, flag ? "true" : "false", ct);
            return true;
        }

        if (StringKeys.Contains(key))
        {
            // Free-form (e.g. the update URL); an empty value clears it.
            await store.SetSettingAsync(key, value.Trim(), ct);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the known setting keys.
    /// </summary>
    public static IReadOnlyList<string> Keys()
    {
        return [.. IntKeys, .. BoolKeys, .. StringKeys];
    }

    private static readonly string[] IntKeys =
        ["refresh-seconds", "connect-timeout-seconds", "dead-threshold-seconds", "failback-probes", "probe-timeout-seconds", "geo-check-interval-hours"];

    private static readonly string[] BoolKeys = ["killswitch", "allow-lan", "geo-auto-check"];

    private static readonly string[] StringKeys = ["update-url"];

    private async Task<string> ReadStringAsync(string key, string fallback, CancellationToken ct)
    {
        var value = await store.GetSettingAsync(key, ct);
        return value ?? fallback;
    }

    private async Task<int> ReadIntAsync(string key, int fallback, CancellationToken ct)
    {
        var value = await store.GetSettingAsync(key, ct);
        return value is not null && int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private async Task<bool> ReadBoolAsync(string key, bool fallback, CancellationToken ct)
    {
        var value = await store.GetSettingAsync(key, ct);
        return value is not null && TryParseBool(value, out var parsed) ? parsed : fallback;
    }

    private static bool TryParseBool(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "true" or "on" or "1" or "yes":
                result = true;
                return true;
            case "false" or "off" or "0" or "no":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
