namespace AmneziaGeo.Ipc;

/// <summary>
/// Access point as the agent takes it.
/// </summary>
public sealed record HotspotOptions
{
    /// <summary>
    /// Whether the access point is asked for.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Network name.
    /// </summary>
    public string Ssid { get; init; } = string.Empty;

    /// <summary>
    /// Network password.
    /// </summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Band asked for: auto, 2.4, or 5.
    /// </summary>
    public string Band { get; init; } = HotspotBands.Auto;

    /// <summary>
    /// Whether the name and the password are both fit to raise the point.
    /// </summary>
    public bool Complete => SettingKeys.IsValidHotspotSsid(Ssid) && SettingKeys.IsValidHotspotPassword(Password);

    /// <summary>
    /// Whether the point should stand right now.
    /// </summary>
    public bool Wanted => Enabled && Complete;

    /// <summary>
    /// Reads the access point out of the stored settings.
    /// </summary>
    public static HotspotOptions Read(IReadOnlyDictionary<string, string> settings)
    {
        return new HotspotOptions
        {
            Enabled = settings.TryGetValue(SettingKeys.ProxyEnabled, out var on)
                && (on.Trim().ToLowerInvariant() is "on" or "true" or "1" or "yes")
                && ShareModes.CarriesWifi(settings.TryGetValue(SettingKeys.ShareMode, out var mode) ? mode : null),
            Ssid = settings.TryGetValue(SettingKeys.HotspotSsid, out var ssid) ? ssid : string.Empty,
            Password = settings.TryGetValue(SettingKeys.HotspotPassword, out var password) ? password : string.Empty,
            Band = HotspotBands.Of(settings.TryGetValue(SettingKeys.HotspotBand, out var band) ? band : null),
        };
    }
}
