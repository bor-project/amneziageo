namespace AmneziaGeo.Ipc;

/// <summary>
/// Auto-switching decisions that depend on nothing but their arguments.
/// </summary>
public static class FailoverPolicy
{
    /// <summary>
    /// Puts the picked server at the head of the priority list, keeping the rest in their order.
    /// </summary>
    public static IReadOnlyList<string> Raise(IEnumerable<string> order, string picked)
    {
        var names = order.ToList();
        var at = names.FindIndex(name => string.Equals(name, picked, StringComparison.Ordinal));
        if (at <= 0)
        {
            return names;
        }

        names.RemoveAt(at);
        names.Insert(0, picked);
        return names;
    }
}
