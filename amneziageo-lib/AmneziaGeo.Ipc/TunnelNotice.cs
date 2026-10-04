using System.Globalization;

namespace AmneziaGeo.Ipc;

/// <summary>
/// What the notification of the tunnel shows.
/// </summary>
public enum NoticeStage
{
    /// <summary>
    /// The session is being raised.
    /// </summary>
    Connecting,

    /// <summary>
    /// The session stands.
    /// </summary>
    Connected,

    /// <summary>
    /// The user took the tunnel down.
    /// </summary>
    Disconnected,

    /// <summary>
    /// The tunnel went down on its own and did not come back.
    /// </summary>
    Stopped,
}

/// <summary>
/// The words of the notification in the language of the window, with what the session routes by. The tunnel
/// process carries no strings of its own, so the head hands them over with the session.
/// </summary>
/// <param name="Connected">Stage word of a session that stands.</param>
/// <param name="Connecting">Stage word of a session being raised.</param>
/// <param name="Attempt">Number of the attempt in flight, {0} is the number.</param>
/// <param name="Disconnected">Stage word after the user took the tunnel down.</param>
/// <param name="Stopped">What is said of a tunnel that went down on its own.</param>
/// <param name="SpeedMbit">Both directions in megabits, {0} down and {1} up.</param>
/// <param name="SpeedKbit">Both directions in kilobits, {0} down and {1} up.</param>
/// <param name="Routing">The routing list in use, {0} is its name.</param>
/// <param name="RoutingOff">What is said when no list is in use.</param>
/// <param name="Disconnect">Label of the action that takes the tunnel down.</param>
/// <param name="Connect">Label of the action that raises it.</param>
/// <param name="ListOff">Label of the action that turns the routing list off.</param>
/// <param name="ListOn">Label of the action that turns a routing list on.</param>
/// <param name="StoppedChannel">Name of the notification category of a stopped tunnel.</param>
/// <param name="Culture">Name of the culture the numbers are written in.</param>
/// <param name="List">Name of the routing list in use, empty when none is.</param>
/// <param name="Offer">Id of the list the action turns on, 0 when there is no list to turn on.</param>
public sealed record NoticeWords(
    string Connected,
    string Connecting,
    string Attempt,
    string Disconnected,
    string Stopped,
    string SpeedMbit,
    string SpeedKbit,
    string Routing,
    string RoutingOff,
    string Disconnect,
    string Connect,
    string ListOff,
    string ListOn,
    string StoppedChannel,
    string Culture,
    string List = "",
    long Offer = 0)
{
    /// <summary>
    /// The words a tunnel raised without a head falls back to.
    /// </summary>
    public static NoticeWords Plain { get; } = new(
        "Connected", "Connecting", "attempt {0}", "Disconnected", "The tunnel has stopped", "↓ {0} ↑ {1} Mbit/s",
        "↓ {0} ↑ {1} kbit/s", "Routing: {0}", "Routing is off", "Disconnect", "Connect", "Turn the list off",
        "Turn the list on", "Tunnel stopped", string.Empty);
}

/// <summary>
/// Composes the lines of the tunnel notification.
/// </summary>
public static class TunnelNotice
{
    private const long Megabit = 1_000_000;
    private const string Separator = " · ";

    /// <summary>
    /// The line under the title: the stage, with the speed of a session that stands and the attempt of one that is
    /// dialled again.
    /// </summary>
    public static string Text(NoticeWords words, NoticeStage stage, int retry, long rxBitsPerSecond, long txBitsPerSecond)
    {
        return stage switch
        {
            NoticeStage.Connected => words.Connected + Separator + Speed(words, rxBitsPerSecond, txBitsPerSecond),
            NoticeStage.Connecting when retry >= 1 => words.Connecting + Separator + Fill(words, words.Attempt, retry + 1),
            NoticeStage.Connecting => words.Connecting,
            NoticeStage.Disconnected => words.Disconnected,
            _ => words.Stopped,
        };
    }

    /// <summary>
    /// The line about the routing list; nothing when there is no list to speak of.
    /// </summary>
    public static string? Routing(NoticeWords words)
    {
        if (words.List.Length > 0)
        {
            return Fill(words, words.Routing, words.List);
        }

        return words.Offer != 0 ? words.RoutingOff : null;
    }

    /// <summary>
    /// The label of the routing action; nothing when there is no list to turn on or off.
    /// </summary>
    public static string? ListAction(NoticeWords words)
    {
        if (words.List.Length > 0)
        {
            return words.ListOff;
        }

        return words.Offer != 0 ? words.ListOn : null;
    }

    // Both directions in the unit the faster of the two calls for.
    private static string Speed(NoticeWords words, long rx, long tx)
    {
        var culture = Culture(words);
        return Math.Max(rx, tx) >= Megabit
            ? string.Format(culture, words.SpeedMbit, (rx / (double)Megabit).ToString("0.0", culture), (tx / (double)Megabit).ToString("0.0", culture))
            : string.Format(culture, words.SpeedKbit, rx / 1000, tx / 1000);
    }

    private static string Fill(NoticeWords words, string pattern, object value)
    {
        return string.Format(Culture(words), pattern, value);
    }

    // The culture the numbers are written in; the invariant one when the name says nothing.
    private static CultureInfo Culture(NoticeWords words)
    {
        try
        {
            return words.Culture.Length > 0 ? CultureInfo.GetCultureInfo(words.Culture) : CultureInfo.InvariantCulture;
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}

/// <summary>
/// Counts how often the guard raised a tunnel whose process was gone, so that one which keeps dying is given up.
/// </summary>
public static class GuardTally
{
    /// <summary>
    /// Raises in a row the guard makes before it gives the tunnel up.
    /// </summary>
    public const int Limit = 3;

    /// <summary>
    /// The count after one more raise; kept as text between two lives of the tunnel process.
    /// </summary>
    public static string Next(string? kept)
    {
        return (Count(kept) + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whether the guard has raised the tunnel as often as it may.
    /// </summary>
    public static bool Spent(string? kept)
    {
        return Count(kept) >= Limit;
    }

    private static int Count(string? kept)
    {
        return int.TryParse(kept?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }
}
