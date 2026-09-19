namespace AmneziaGeo.Ipc;

/// <summary>
/// Whether an app rule names a given application, decided from the rule text alone: the same package, image,
/// folder or service the agent routes by, without a running process to look at.
/// </summary>
public static class AppRuleCover
{
    private static readonly char[] _slashes = ['\\', '/'];

    /// <summary>
    /// The kind and the value of an app rule or target: "pkg", "dir", "path", "svc" or "name", and the text
    /// after it. A bare value is a path, as the matcher reads it.
    /// </summary>
    public static (string Kind, string Value) Parse(string token)
    {
        var (kind, value, _) = Split(token);
        return (kind, value);
    }

    /// <summary>
    /// Whether the rule names the target: the same package, the same image, the same service, or a folder the
    /// target sits in.
    /// </summary>
    public static bool Covers(string rule, string target)
    {
        var (ruleKind, ruleValue, _) = Split(rule);
        var (targetKind, targetValue, named) = Split(target);
        if (ruleValue.Length == 0 || targetValue.Length == 0)
        {
            return false;
        }

        // A bare target without a separator names either an image or a package family.
        if (!named && targetValue.IndexOfAny(_slashes) < 0)
        {
            return CoversName(ruleKind, ruleValue, targetValue)
                || CoversPackage(ruleKind, ruleValue, Family(targetValue));
        }

        return targetKind switch
        {
            "pkg" => CoversPackage(ruleKind, ruleValue, Family(targetValue)),
            "svc" => ruleKind == "svc" && Same(ruleValue, targetValue),
            "name" => CoversName(ruleKind, ruleValue, targetValue),
            "dir" => CoversDir(ruleKind, ruleValue, Tokenized(targetValue)),
            _ => CoversPath(ruleKind, ruleValue, Tokenized(targetValue)),
        };
    }

    // A rule naming the same package: a package rule outright, or a folder or image inside that package.
    private static bool CoversPackage(string kind, string value, string family)
    {
        return kind switch
        {
            "pkg" => Same(Family(value), family),
            "dir" or "path" => AppPathToken.PackageFamilyFromPath(Tokenized(value)) is { } own && Same(own, family),
            _ => false,
        };
    }

    // A rule naming the same image file.
    private static bool CoversName(string kind, string value, string name)
    {
        return kind switch
        {
            "name" => Same(value, name),
            "path" => Same(LeafOf(Tokenized(value)), name),
            _ => false,
        };
    }

    // A rule naming the image itself, a folder above it, its package or its file name.
    private static bool CoversPath(string kind, string value, string path)
    {
        return kind switch
        {
            "path" => Same(Tokenized(value), path),
            "name" => Same(value, LeafOf(path)),
            "dir" => Under(AppPathToken.StripVersionedLeaf(Tokenized(value)), path),
            "pkg" => AppPathToken.PackageFamilyFromPath(path) is { } family && Same(Family(value), family),
            _ => false,
        };
    }

    // A rule naming the folder itself, one above it, or the package it holds.
    private static bool CoversDir(string kind, string value, string dir)
    {
        return kind switch
        {
            "dir" => Same(AppPathToken.StripVersionedLeaf(Tokenized(value)), AppPathToken.StripVersionedLeaf(dir))
                || Under(AppPathToken.StripVersionedLeaf(Tokenized(value)), dir),
            "path" => Under(dir, Tokenized(value)),
            "pkg" => AppPathToken.PackageFamilyFromPath(dir) is { } family && Same(Family(value), family),
            _ => false,
        };
    }

    // "app:pkg=value" -> ("pkg", "value", true); a bare value is a path.
    private static (string Kind, string Value, bool Named) Split(string token)
    {
        var text = token.Trim();
        if (text.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..];
        }

        var eq = text.IndexOf('=');
        return eq <= 0
            ? ("path", text.Trim(), false)
            : (text[..eq].Trim().ToLowerInvariant(), text[(eq + 1)..].Trim(), true);
    }

    // A package full name reduced to its family, a family as it stands.
    private static string Family(string value) => AppPathToken.PackageFamilyFromFullName(value) ?? value;

    // The %ENV% form the rules are stored in, without a trailing slash.
    private static string Tokenized(string value) => AppPathToken.Tokenize(value.Trim().TrimEnd(_slashes));

    private static string LeafOf(string path)
    {
        var slash = path.LastIndexOfAny(_slashes);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static bool Under(string dir, string path) =>
        path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase);

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
