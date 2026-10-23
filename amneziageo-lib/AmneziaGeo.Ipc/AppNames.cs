namespace AmneziaGeo.Ipc;

/// <summary>
/// A program of the device the way a search sees it: what people call it, its file, its package and the app rule
/// value that routes it ("path=...", "dir=..." or "pkg=...").
/// </summary>
/// <param name="Label">The name the program is shown under, empty when it has none.</param>
/// <param name="File">The file of the program, empty when it has none.</param>
/// <param name="Package">The package of the program, empty when it has none.</param>
/// <param name="Value">The app rule value that routes the program.</param>
public sealed record NamedApp(string Label, string File, string Package, string Value);

/// <summary>
/// The form app:&lt;name&gt; of an app rule: a program named the way people know it, which the device finds itself.
/// </summary>
public static class AppNames
{
    private static readonly char[] _slashes = ['\\', '/'];

    /// <summary>
    /// Tells whether an app rule value names a program to find: no kind before '=' and no folder in it.
    /// </summary>
    public static bool IsName(string value)
    {
        var text = value.Trim();

        return text.Length > 0 && text.IndexOf('=', StringComparison.Ordinal) < 0 && text.IndexOfAny(_slashes) < 0;
    }

    /// <summary>
    /// Returns the values a device routes by: a name stands for what was found for it and for nothing when nothing
    /// was, the other values stand as they are.
    /// </summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string> values, IReadOnlyDictionary<string, IReadOnlyList<string>> found)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(found);

        var result = new List<string>();
        foreach (var value in values)
        {
            if (!IsName(value))
            {
                result.Add(value);
            }
            else if (found.TryGetValue(value.Trim(), out var programs))
            {
                result.AddRange(programs);
            }
        }

        return [.. result.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Returns the values of the programs a name stands for: every program whose label, file without its extension
    /// or package is the name, else the programs of the only label that starts with the name as a word.
    /// </summary>
    public static IReadOnlyList<string> Pick(string name, IReadOnlyList<NamedApp> programs)
    {
        ArgumentNullException.ThrowIfNull(programs);

        var wanted = name.Trim();
        if (wanted.Length == 0)
        {
            return [];
        }

        var same = programs.Where(app => Same(app.Label, wanted) || Same(Stem(app.File), wanted) || Same(app.Package, wanted)).ToList();
        if (same.Count > 0)
        {
            return Values(same);
        }

        var led = programs.Where(app => Leads(app.Label, wanted)).ToList();

        return led.Select(app => app.Label.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? Values(led) : [];
    }

    private static IReadOnlyList<string> Values(IEnumerable<NamedApp> programs) =>
        [.. programs.Select(app => app.Value).Where(value => !OwnAppRule.Names("app:" + value)).Distinct(StringComparer.OrdinalIgnoreCase)];

    // The file name without its folder and extension.
    private static string Stem(string file)
    {
        var leaf = file[(file.LastIndexOfAny(_slashes) + 1)..];
        var dot = leaf.LastIndexOf('.');

        return dot > 0 ? leaf[..dot] : leaf;
    }

    private static bool Same(string text, string name) => string.Equals(text.Trim(), name, StringComparison.OrdinalIgnoreCase);

    // A label that starts with the name followed by something that is not a letter or a digit.
    private static bool Leads(string label, string name)
    {
        var text = label.Trim();

        return text.Length > name.Length
            && text.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            && !char.IsLetterOrDigit(text[name.Length]);
    }
}
