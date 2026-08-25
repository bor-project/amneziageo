namespace AmneziaGeo.Ipc;

/// <summary>
/// Radio band the access point asks for. Only the band travels: a channel chosen by hand keeps the point from
/// coming up.
/// </summary>
public static class HotspotBands
{
    /// <summary>
    /// Band left to the adapter.
    /// </summary>
    public const string Auto = "auto";

    /// <summary>
    /// 2.4 GHz: reaches further and old devices see it.
    /// </summary>
    public const string TwoPointFour = "2.4";

    /// <summary>
    /// 5 GHz: carries video.
    /// </summary>
    public const string Five = "5";

    /// <summary>
    /// Reads a band token, answering auto for anything else.
    /// </summary>
    public static string Of(string? text)
    {
        var token = text?.Trim().ToLowerInvariant();
        return token is Auto or TwoPointFour or Five ? token : Auto;
    }

    /// <summary>
    /// Whether a token names a band.
    /// </summary>
    public static bool IsKnown(string? text)
    {
        var token = text?.Trim().ToLowerInvariant();
        return token is Auto or TwoPointFour or Five;
    }
}
