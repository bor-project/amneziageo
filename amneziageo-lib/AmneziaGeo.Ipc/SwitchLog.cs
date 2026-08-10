namespace AmneziaGeo.Ipc;

/// <summary>
/// The two changes a support thread reads first: which server the tunnel is bound to, and which routing list
/// decides where its traffic goes. Both are stored whatever the capture floor is - they are rare, and without
/// them a list swapped behind the user's back is only found by hand.
/// </summary>
public static class SwitchLog
{
    /// <summary>
    /// Source both lines carry in the log.
    /// </summary>
    public const string Source = "switch";

    /// <summary>
    /// Severity the lines are stored at, in the dictionary ids of the log store.
    /// </summary>
    public const int LevelId = 4;

    /// <summary>
    /// The line for a changed server, or null when the selection did not move.
    /// </summary>
    public static string? Config(string? from, string? to)
    {
        return Moved(from, to) ? $"active server: {Name(to)} (was {Name(from)})" : null;
    }

    /// <summary>
    /// The line for a changed routing list, or null when the selection did not move.
    /// </summary>
    public static string? RoutingList(string? from, string? to)
    {
        return Moved(from, to) ? $"routing list: {Name(to)} (was {Name(from)})" : null;
    }

    private static bool Moved(string? from, string? to)
    {
        return !string.Equals(from ?? string.Empty, to ?? string.Empty, StringComparison.Ordinal);
    }

    private static string Name(string? value) => value is { Length: > 0 } ? $"\"{value}\"" : "none";
}
