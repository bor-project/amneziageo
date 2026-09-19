namespace AmneziaGeo.Ipc;

/// <summary>
/// How the system is made to reach the name proxy of this machine.
/// </summary>
public static class LocalDohModes
{
    /// <summary>
    /// Put the proxy on HTTPS only once the system's lookups stop reaching it on plain 53.
    /// </summary>
    public const string Auto = "auto";

    /// <summary>
    /// Leave the system on plain 53 whatever happens to it.
    /// </summary>
    public const string Off = "off";

    /// <summary>
    /// Put the proxy on HTTPS as soon as the tunnel is up.
    /// </summary>
    public const string On = "on";

    /// <summary>
    /// What a fresh install uses.
    /// </summary>
    public const string Default = Auto;

    /// <summary>
    /// Every mode, in the order a picker lists them.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Auto, Off, On];

    /// <summary>
    /// The mode this text names, Default when it names none.
    /// </summary>
    public static string Of(string? value)
    {
        return IsKnown(value) ? value!.Trim().ToLowerInvariant() : Default;
    }

    /// <summary>
    /// Whether this text names a mode.
    /// </summary>
    public static bool IsKnown(string? value)
    {
        var mode = value?.Trim().ToLowerInvariant();
        return mode is Auto or Off or On;
    }
}
