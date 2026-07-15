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
        var values = await store.GetSettingsAsync(ct);
        return new AppSettings
        {
            RefreshSeconds = ReadInt(values, "refresh-seconds", defaults.RefreshSeconds),
            ConnectTimeoutSeconds = ReadInt(values, "connect-timeout-seconds", defaults.ConnectTimeoutSeconds),
            DeadThresholdSeconds = ReadInt(values, "dead-threshold-seconds", defaults.DeadThresholdSeconds),
            // Update URL is baked into the build; a stale persisted row must not shadow it.
            UpdateUrl = defaults.UpdateUrl,
            GeoAutoCheck = ReadBool(values, "geo-auto-check", defaults.GeoAutoCheck),
            GeoCheckIntervalHours = ReadInt(values, "geo-check-interval-hours", defaults.GeoCheckIntervalHours),
            GeoCacheValidityHours = ReadInt(values, "geo-cache-validity-hours", defaults.GeoCacheValidityHours),
            TunnelAllUdp = ReadBool(values, "tunnel-all-udp", defaults.TunnelAllUdp),
            LogLevel = ReadLogLevel(values, defaults.LogLevel),
            RouteLog = ReadBool(values, RouteLog.SettingKey, defaults.RouteLog),
            SurviveReboot = ReadBool(values, "survive-reboot", defaults.SurviveReboot),
            PeriodicReconnect = ReadBool(values, "periodic-reconnect-enabled", defaults.PeriodicReconnect),
            PeriodicReconnectIntervalSeconds = ReadInt(values, "periodic-reconnect-interval-seconds", defaults.PeriodicReconnectIntervalSeconds),
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
            // Log level accepts only the exposed tokens; a bad value is rejected, stored lowercase.
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

            // Free-form string settings; an empty value clears it.
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
        ["refresh-seconds", "connect-timeout-seconds", "dead-threshold-seconds", "geo-check-interval-hours", "geo-cache-validity-hours", "periodic-reconnect-interval-seconds"];

    private static readonly string[] BoolKeys = ["geo-auto-check", "tunnel-all-udp", RouteLog.SettingKey, "survive-reboot", "periodic-reconnect-enabled"];

    // Validated string settings; log-level is constrained to verbosity tokens.
    private static readonly string[] StringKeys = [LogLevelWatcher.SettingKey];

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
        => values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
        => values.TryGetValue(key, out var value) && TryParseBool(value, out var parsed) ? parsed : fallback;

    private static string ReadLogLevel(IReadOnlyDictionary<string, string> values, string fallback)
        => values.TryGetValue(LogLevelWatcher.SettingKey, out var value) && LogLevelController.IsValid(value) ? value : fallback;

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
