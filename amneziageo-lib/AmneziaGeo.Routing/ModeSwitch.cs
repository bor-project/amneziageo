namespace AmneziaGeo.Routing;

/// <summary>
/// What the machine is left standing on once several servers stop working at once: the server carrying the default
/// route stays up and the rest go down. Nothing else moves - the priority and the cards switched off are what the
/// mode comes back to.
/// </summary>
/// <param name="Keeper">Server carrying everything afterwards; empty when nothing is up.</param>
/// <param name="Dropped">Tunnels to take down, in the order they were given.</param>
public sealed record ModeSwitch(string Keeper, IReadOnlyList<string> Dropped)
{
    /// <summary>
    /// Nothing is up, so nothing stays and nothing goes.
    /// </summary>
    public static readonly ModeSwitch Empty = new(string.Empty, []);

    /// <summary>
    /// Works out who stays up and who goes down.
    /// </summary>
    /// <param name="order">Configurations in priority order.</param>
    /// <param name="up">Tunnels up right now.</param>
    /// <param name="holder">Server carrying the default route, if any.</param>
    public static ModeSwitch Settle(IReadOnlyList<string> order, IReadOnlyList<string> up, string? holder)
    {
        if (up.Count == 0)
        {
            return Empty;
        }

        var keeper = holder is { Length: > 0 } && up.Contains(holder, StringComparer.Ordinal) ? holder : First(order, up);
        return new ModeSwitch(keeper, [.. up.Where(name => !string.Equals(name, keeper, StringComparison.Ordinal))]);
    }

    // Nobody carries the default route, so the first server up the priority stays; one the priority does not know
    // stays only where it is the first thing up.
    private static string First(IReadOnlyList<string> order, IReadOnlyList<string> up)
    {
        return order.FirstOrDefault(name => up.Contains(name, StringComparer.Ordinal)) ?? up[0];
    }
}
