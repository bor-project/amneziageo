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
    /// Both directions in one short reading, for a card that has an icon in place of the words.
    /// </summary>
    public static string Compact(long rxBitsPerSecond, long txBitsPerSecond)
    {
        return Math.Max(rxBitsPerSecond, txBitsPerSecond) >= Megabit
            ? Loc.Instance.Get("Main_CardSpeedMbit", Megabits(rxBitsPerSecond), Megabits(txBitsPerSecond))
            : Loc.Instance.Get("Main_CardSpeedKbit", rxBitsPerSecond / 1000, txBitsPerSecond / 1000);
    }

    private static string Megabits(long bitsPerSecond)
    {
        return (bitsPerSecond / (double)Megabit).ToString("0.0", CultureInfo.CurrentCulture);
    }
}
