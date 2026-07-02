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
            // The update URL is baked into the build (installer config), not a persisted/user setting, so it
            // is always the baked default - a stale 'update-url' row from older builds must not shadow it.
            UpdateUrl = defaults.UpdateUrl,
            GeoAutoCheck = await ReadBoolAsync("geo-auto-check", defaults.GeoAutoCheck, ct),
            GeoCheckIntervalHours = await ReadIntAsync("geo-check-interval-hours", defaults.GeoCheckIntervalHours, ct),
            GeoCacheValidityHours = await ReadIntAsync("geo-cache-validity-hours", defaults.GeoCacheValidityHours, ct),
            TunnelAllUdp = await ReadBoolAsync("tunnel-all-udp", defaults.TunnelAllUdp, ct),
            LogLevel = await ReadLogLevelAsync(defaults.LogLevel, ct),
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
            var trimmed = value.Trim();
            // The log level accepts only the three exposed tokens; a bad value is rejected so the live switch
            // never lands in an unknown state. Stored lowercase-canonical.
            if (key == LogLevelWatcher.SettingKey)
            {
                var token = trimmed.ToLowerInvariant();
                if (!LogLevelController.IsValid(token))
                {
                    return false;
                }

                await store.SetSettingAsync(key, token, ct);
                return true;
            }

            // Other free-form string settings; an empty value clears it.
            await store.SetSettingAsync(key, trimmed, ct);
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
        ["refresh-seconds", "connect-timeout-seconds", "dead-threshold-seconds", "failback-probes", "probe-timeout-seconds", "geo-check-interval-hours", "geo-cache-validity-hours"];

    private static readonly string[] BoolKeys = ["geo-auto-check", "tunnel-all-udp"];

    // Validated string settings. "log-level" (#82) is constrained to the three exposed verbosity tokens.
    private static readonly string[] StringKeys = [LogLevelWatcher.SettingKey];

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

    private async Task<string> ReadLogLevelAsync(string fallback, CancellationToken ct)
    {
        var value = await store.GetSettingAsync(LogLevelWatcher.SettingKey, ct);
        return LogLevelController.IsValid(value) ? value! : fallback;
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
