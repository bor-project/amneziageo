namespace AmneziaGeo.Linux.App;

/// <summary>
/// The executables an application rule names: a whole path, everything under a directory, or a file name wherever
/// it lives. A bare value is read as a path.
/// </summary>
internal sealed class AppImages
{
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private readonly List<string> _directories = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the rules name no executable at all.
    /// </summary>
    public bool Empty => _paths.Count == 0 && _directories.Count == 0 && _names.Count == 0;

    /// <summary>
    /// How many matchers the rules carry.
    /// </summary>
    public int Count => _paths.Count + _directories.Count + _names.Count;

    /// <summary>
    /// Reads the app rule values of a routing list.
    /// </summary>
    public static AppImages Parse(IReadOnlyList<string> tokens)
    {
        var images = new AppImages();
        foreach (var raw in tokens)
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            var equals = token.IndexOf('=');
            if (equals <= 0)
            {
                images._paths.Add(token);
                continue;
            }

            var kind = token[..equals].Trim().ToLowerInvariant();
            var value = token[(equals + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            switch (kind)
            {
                case "path":
                    images._paths.Add(value);
                    break;
                case "dir":
                    images._directories.Add(value.TrimEnd('/') + '/');
                    break;
                case "name":
                    images._names.Add(value);
                    break;
                case "pkg":
                    // A package name is what the phone calls an application; here the same rule names a file.
                    images._names.Add(value);
                    break;
            }
        }

        return images;
    }

    /// <summary>
    /// Whether one executable is named by the rules.
    /// </summary>
    public bool Names(string image)
    {
        if (_paths.Contains(image) || _names.Contains(Path.GetFileName(image)))
        {
            return true;
        }

        foreach (var directory in _directories)
        {
            if (image.StartsWith(directory, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
