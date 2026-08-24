namespace AmneziaGeo.Ipc;

/// <summary>
/// Configuration names carried in one setting, one per line.
/// </summary>
public static class NameList
{
    /// <summary>
    /// Reads the names, dropping blank lines and surrounding spaces.
    /// </summary>
    public static IReadOnlyList<string> Split(string? text)
    {
        return text is null
            ? []
            : text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Folds the names back into one setting value.
    /// </summary>
    public static string Join(IEnumerable<string> names)
    {
        return string.Join(Environment.NewLine, names);
    }

    /// <summary>
    /// Drops the names no configuration answers to any more, keeping the order of the rest.
    /// </summary>
    public static string Prune(string? text, IEnumerable<string> present)
    {
        var known = present.ToHashSet(StringComparer.Ordinal);
        return Join(Split(text).Where(known.Contains));
    }
}
