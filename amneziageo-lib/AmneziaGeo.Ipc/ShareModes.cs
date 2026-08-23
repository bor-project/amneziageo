namespace AmneziaGeo.Ipc;

/// <summary>
/// How this machine hands the tunnel to other devices.
/// </summary>
public static class ShareModes
{
    /// <summary>
    /// Proxy on the network this machine is already in.
    /// </summary>
    public const string Lan = "lan";

    /// <summary>
    /// Own Wi-Fi access point.
    /// </summary>
    public const string Wifi = "wifi";

    /// <summary>
    /// Both at once.
    /// </summary>
    public const string Both = "both";

    /// <summary>
    /// Mode in force where none was chosen.
    /// </summary>
    public const string Default = Both;

    /// <summary>
    /// Reads a mode token, answering the default for anything else.
    /// </summary>
    public static string Of(string? text)
    {
        var token = text?.Trim().ToLowerInvariant();
        return token is Lan or Wifi or Both ? token : Default;
    }

    /// <summary>
    /// Whether a token names a mode.
    /// </summary>
    public static bool IsKnown(string? text)
    {
        var token = text?.Trim().ToLowerInvariant();
        return token is Lan or Wifi or Both;
    }

    /// <summary>
    /// Whether the mode serves the network this machine is in.
    /// </summary>
    public static bool CarriesLan(string? mode)
    {
        return Of(mode) is Lan or Both;
    }

    /// <summary>
    /// Whether the mode raises an access point.
    /// </summary>
    public static bool CarriesWifi(string? mode)
    {
        return Of(mode) is Wifi or Both;
    }
}
