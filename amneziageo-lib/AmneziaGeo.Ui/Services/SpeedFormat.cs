using System.Globalization;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Formats transfer rates for the screen.
/// </summary>
internal static class SpeedFormat
{
    private const long Megabit = 1_000_000;

    /// <summary>
    /// Both directions on one line, in the unit the faster of the two calls for.
    /// </summary>
    public static string Pair(long rxBitsPerSecond, long txBitsPerSecond)
    {
        return Math.Max(rxBitsPerSecond, txBitsPerSecond) >= Megabit
            ? Loc.Instance.Get("Main_LinkSpeedMbit", Megabits(rxBitsPerSecond), Megabits(txBitsPerSecond))
            : Loc.Instance.Get("Main_LinkSpeedKbit", rxBitsPerSecond / 1000, txBitsPerSecond / 1000);
    }

    /// <summary>
    /// One direction as a bare number, in the unit the faster of the two calls for: the pair stands in two
    /// tiles under one unit, so the scale is picked over both.
    /// </summary>
    public static string Value(long bitsPerSecond, long peerBitsPerSecond)
    {
        return Math.Max(bitsPerSecond, peerBitsPerSecond) >= Megabit
            ? Megabits(bitsPerSecond)
            : (bitsPerSecond / 1000).ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The unit both directions are written in.
    /// </summary>
    public static string Unit(long bitsPerSecond, long peerBitsPerSecond)
    {
        return Loc.Instance.Get(Math.Max(bitsPerSecond, peerBitsPerSecond) >= Megabit
            ? "Main_UnitMbit"
            : "Main_UnitKbit");
    }

    private static string Megabits(long bitsPerSecond)
    {
        return (bitsPerSecond / (double)Megabit).ToString("0.0", CultureInfo.CurrentCulture);
    }
}
