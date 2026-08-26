namespace AmneziaGeo.Routing;

/// <summary>
/// Which server carries everything. The one holding it keeps it while its dialling still has attempts left, and
/// the next server up the priority takes over once they are spent; the one that fell stays up and keeps trying.
/// </summary>
public static class RouteCarrier
{
    /// <summary>
    /// Picks the server everything rides on.
    /// </summary>
    /// <param name="up">Servers up, priority top down.</param>
    /// <param name="holder">Server carrying everything right now.</param>
    /// <param name="spent">Servers whose dialling is spent.</param>
    public static string? Pick(IReadOnlyList<string> up, string? holder, IReadOnlySet<string> spent)
    {
        if (up.Count == 0)
        {
            return null;
        }

        var fresh = up.FirstOrDefault(name => !spent.Contains(name));
        if (holder is { Length: > 0 } && up.Contains(holder, StringComparer.Ordinal))
        {
            // Everything stays where it is while that server still has dials left, and while nobody else has any.
            return spent.Contains(holder) && fresh is not null ? fresh : holder;
        }

        return fresh ?? up[0];
    }

    /// <summary>
    /// Puts the server carrying everything at the head of the servers up.
    /// </summary>
    public static IReadOnlyList<string> Head(IReadOnlyList<string> up, string? carrier)
    {
        if (carrier is null || up.Count == 0 || string.Equals(up[0], carrier, StringComparison.Ordinal))
        {
            return up;
        }

        var ordered = up.ToList();
        if (!ordered.Remove(carrier))
        {
            return up;
        }

        ordered.Insert(0, carrier);
        return ordered;
    }
}
