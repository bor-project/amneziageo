using AmneziaGeo.Ipc;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// An app rule that catches nothing: nothing installed carries it, or a twin package runs the same program.
/// </summary>
internal readonly record struct AppRuleNote(string Rule, string? Twin);

/// <summary>
/// Checks app rules against what is installed here.
/// </summary>
internal static class AppRuleAudit
{
    private static readonly char[] _slashes = ['\\', '/'];

    /// <summary>
    /// Returns the publishers of the packages the rules name.
    /// </summary>
    public static IReadOnlySet<string> Publishers(IReadOnlyList<string> rules)
    {
        var publishers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (FamilyOf(rule) is { } family)
            {
                publishers.Add(PublisherOf(family));
            }
        }

        return publishers;
    }

    /// <summary>
    /// Returns a note per rule naming nothing installed, and per package rule whose program another installed
    /// package runs as well while no rule names that one.
    /// </summary>
    public static IReadOnlyList<AppRuleNote> Check(IReadOnlyList<string> rules, IReadOnlyList<PackageEntry> packages, Func<string, bool> dirExists, Func<string, bool> fileExists, Func<string, bool> serviceExists)
    {
        var ruled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (FamilyOf(rule) is { } family)
            {
                ruled.Add(family);
            }
        }

        var notes = new List<AppRuleNote>();
        foreach (var rule in rules)
        {
            var (kind, value) = AppRuleCover.Parse(rule);
            if (value.Length == 0)
            {
                continue;
            }

            if (FamilyOf(rule) is { } family)
            {
                notes.AddRange(PackageNotes(rule, family, packages, ruled));
                continue;
            }

            var missing = kind switch
            {
                "dir" => !dirExists(AppPathToken.StripVersionedLeaf(AppPathToken.Tokenize(value.TrimEnd(_slashes)))),
                "path" => !fileExists(AppPathToken.Tokenize(value)),
                "svc" => !serviceExists(value),
                _ => false,
            };
            if (missing)
            {
                notes.Add(new AppRuleNote(rule, null));
            }
        }

        return notes;
    }

    // A package rule: nothing installed with that family, or a twin of the same publisher running the same program.
    private static IEnumerable<AppRuleNote> PackageNotes(string rule, string family, IReadOnlyList<PackageEntry> packages, HashSet<string> ruled)
    {
        var installed = packages.FirstOrDefault(package => package.Family.Equals(family, StringComparison.OrdinalIgnoreCase));
        if (installed.Family is null)
        {
            yield return new AppRuleNote(rule, null);
            yield break;
        }

        foreach (var other in packages)
        {
            if (other.Family.Equals(family, StringComparison.OrdinalIgnoreCase)
                || !other.Publisher.Equals(installed.Publisher, StringComparison.OrdinalIgnoreCase)
                || ruled.Contains(other.Family)
                || !other.Executables.Any(program => installed.Executables.Contains(program, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return new AppRuleNote(rule, other.Family);
        }
    }

    // The package family a rule resolves to: a pkg rule as it stands, a folder or path by the package holding it.
    private static string? FamilyOf(string rule)
    {
        var (kind, value) = AppRuleCover.Parse(rule);
        if (value.Length == 0)
        {
            return null;
        }

        return kind switch
        {
            "pkg" => value,
            "dir" or "path" => AppPathToken.PackageFamilyFromPath(AppPathToken.Tokenize(value.TrimEnd(_slashes))),
            _ => null,
        };
    }

    // "Name_PublisherId" -> "PublisherId".
    private static string PublisherOf(string family)
    {
        var underscore = family.LastIndexOf('_');
        return underscore > 0 ? family[(underscore + 1)..] : string.Empty;
    }

}
