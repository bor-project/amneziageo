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
            RouteTtlSeconds = ReadInt(values, AppSettings.RouteTtlKey, defaults.RouteTtlSeconds),
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
            ShowNotifications = ReadBool(values, "show-notifications", defaults.ShowNotifications),
            AllowPrerelease = ReadBool(values, "allow-prerelease", defaults.AllowPrerelease),
            MultiServer = ReadBool(values, AmneziaGeo.Ipc.SettingKeys.MultiServer, defaults.MultiServer),
            BalanceIntervalSeconds = Math.Clamp(ReadInt(values, "balance-interval-seconds", defaults.BalanceIntervalSeconds), 5, 3600),
            BalanceStrikes = Math.Clamp(ReadInt(values, "balance-strikes", defaults.BalanceStrikes), 1, 10),
            BalanceMarginPercent = Math.Clamp(ReadInt(values, "balance-margin-percent", defaults.BalanceMarginPercent), 1, 100),
            ProxyEnabled = ReadBool(values, AmneziaGeo.Ipc.SettingKeys.ProxyEnabled, defaults.ProxyEnabled),
            ProxySocksPort = ReadInt(values, AmneziaGeo.Ipc.SettingKeys.ProxySocksPort, defaults.ProxySocksPort),
            ProxyHttpPort = ReadInt(values, AmneziaGeo.Ipc.SettingKeys.ProxyHttpPort, defaults.ProxyHttpPort),
            ProxyAnonymous = ReadBool(values, AmneziaGeo.Ipc.SettingKeys.ProxyAnonymous, defaults.ProxyAnonymous),
            ProxyCredentials = ReadCredentials(values, defaults.ProxyCredentials),
            ShareMode = AmneziaGeo.Ipc.ShareModes.Of(ReadText(values, AmneziaGeo.Ipc.SettingKeys.ShareMode, defaults.ShareMode)),
            ShareEthernet = ReadBool(values, AmneziaGeo.Ipc.SettingKeys.ShareEthernet, defaults.ShareEthernet),
            HotspotSsid = ReadText(values, AmneziaGeo.Ipc.SettingKeys.HotspotSsid, defaults.HotspotSsid),
            HotspotPassword = ReadText(values, AmneziaGeo.Ipc.SettingKeys.HotspotPassword, defaults.HotspotPassword),
            HotspotBand = AmneziaGeo.Ipc.HotspotBands.Of(ReadText(values, AmneziaGeo.Ipc.SettingKeys.HotspotBand, defaults.HotspotBand)),
        };
    }

    // The single user/password pair became a list; a pair left over from an earlier version becomes its first account.
    private static string ReadCredentials(IReadOnlyDictionary<string, string> values, string fallback)
    {
        var stored = ReadText(values, AmneziaGeo.Ipc.SettingKeys.ProxyCredentials, string.Empty);
        if (stored.Length > 0)
        {
            return stored;
        }

        var user = ReadText(values, AmneziaGeo.Ipc.SettingKeys.ProxyUser, string.Empty).Trim();
        return user.Length > 0
            ? $"{user}:{ReadText(values, AmneziaGeo.Ipc.SettingKeys.ProxyPassword, string.Empty)}"
            : fallback;
    }

    /// <summary>
    /// Sets a known setting, returning false for an unknown key or an invalid value. Integer settings
    /// must be positive; boolean settings accept true/false/on/off/1/0/yes/no.
    /// </summary>
    public async Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
        if (key == AppSettings.RouteTtlKey)
        {
            // One rule for the editor and the store: a lifetime the UI accepts is one this persists.
            if (!AmneziaGeo.Ipc.SettingKeys.TryParseRouteTtl(value, out var ttl))
            {
                return false;
            }

            await store.SetSettingAsync(key, ttl.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
            return true;
        }

        if (ProxyPortKeys.Contains(key))
        {
            if (!AmneziaGeo.Ipc.SettingKeys.TryParseProxyPort(value, out var port))
            {
                return false;
            }

            await store.SetSettingAsync(key, port.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
            return true;
        }

        if (key == AmneziaGeo.Ipc.SettingKeys.ShareMode)
        {
            if (!AmneziaGeo.Ipc.ShareModes.IsKnown(value))
            {
                return false;
            }

            await store.SetSettingAsync(key, AmneziaGeo.Ipc.ShareModes.Of(value), ct);
            return true;
        }

        if (key == AmneziaGeo.Ipc.SettingKeys.HotspotBand)
        {
            if (!AmneziaGeo.Ipc.HotspotBands.IsKnown(value))
            {
                return false;
            }

            await store.SetSettingAsync(key, AmneziaGeo.Ipc.HotspotBands.Of(value), ct);
            return true;
        }

        // The name and the password of the access point are stored as they came: a trim would take a space
        // the user meant out of a password. An empty value clears the setting and drops the point.
        if (key == AmneziaGeo.Ipc.SettingKeys.HotspotSsid)
        {
            if (value.Length > 0 && !AmneziaGeo.Ipc.SettingKeys.IsValidHotspotSsid(value))
            {
                return false;
            }

            await store.SetSettingAsync(key, value, ct);
            return true;
        }

        if (key == AmneziaGeo.Ipc.SettingKeys.HotspotPassword)
        {
            if (value.Length > 0 && !AmneziaGeo.Ipc.SettingKeys.IsValidHotspotPassword(value))
            {
                return false;
            }

            await store.SetSettingAsync(key, value, ct);
            return true;
        }

        if (IntKeys.Contains(key))
        {
            // Zero is a real setting only where it means "hold nothing"; elsewhere it would stall a loop.
            var floor = ZeroableIntKeys.Contains(key) ? 0 : 1;
            if (!int.TryParse(value, out var parsed) || parsed < floor)
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
        return [.. IntKeys, .. ProxyPortKeys, .. BoolKeys, .. StringKeys, .. ShareKeys];
    }

    private static readonly string[] IntKeys =
        [AppSettings.RouteTtlKey, "connect-timeout-seconds", "dead-threshold-seconds", "geo-check-interval-hours", "geo-cache-validity-hours", "periodic-reconnect-interval-seconds", "balance-interval-seconds", "balance-strikes", "balance-margin-percent"];

    // Integer settings that accept 0.
    private static readonly string[] ZeroableIntKeys = [AppSettings.RouteTtlKey];

    // Listening ports, which take their own range instead of the positive-integer rule.
    private static readonly string[] ProxyPortKeys =
        [AmneziaGeo.Ipc.SettingKeys.ProxySocksPort, AmneziaGeo.Ipc.SettingKeys.ProxyHttpPort];

    private static readonly string[] BoolKeys = ["geo-auto-check", "tunnel-all-udp", RouteLog.SettingKey, "survive-reboot", "periodic-reconnect-enabled", "show-notifications", "allow-prerelease", AmneziaGeo.Ipc.SettingKeys.ProxyEnabled, AmneziaGeo.Ipc.SettingKeys.ProxyAnonymous, AmneziaGeo.Ipc.SettingKeys.ShareEthernet, AmneziaGeo.Ipc.SettingKeys.MultiServer];

    // Sharing settings, each with a rule of its own.
    private static readonly string[] ShareKeys =
        [AmneziaGeo.Ipc.SettingKeys.ShareMode, AmneziaGeo.Ipc.SettingKeys.HotspotSsid, AmneziaGeo.Ipc.SettingKeys.HotspotPassword, AmneziaGeo.Ipc.SettingKeys.HotspotBand];

    // Validated string settings; log-level is constrained to verbosity tokens.
    private static readonly string[] StringKeys = [LogLevelWatcher.SettingKey, AmneziaGeo.Ipc.SettingKeys.ProxyCredentials];

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
        => values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
        => values.TryGetValue(key, out var value) && TryParseBool(value, out var parsed) ? parsed : fallback;

    private static string ReadText(IReadOnlyDictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out var value) ? value : fallback;

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
