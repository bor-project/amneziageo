using System.IO;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Finds application files and folders for a routing input.
/// </summary>
internal static class RoutingPathSuggestions
{
    internal sealed record Match(string Token, bool Missing);

    /// <summary>
    /// Reads matching paths and retains the entered path.
    /// </summary>
    public static IReadOnlyList<Match> Find(string input, int limit = 8)
    {
        var value = input.Trim();
        var kind = string.Empty;
        if (value.StartsWith("app:path=", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("app:dir=", StringComparison.OrdinalIgnoreCase))
        {
            var equals = value.IndexOf('=');
            kind = value[4..equals].ToLowerInvariant();
            value = value[(equals + 1)..].Trim();
        }

        value = value.Trim('"');
        var path = Environment.ExpandEnvironmentVariables(value);
        if (!Path.IsPathFullyQualified(path))
        {
            return [];
        }

        var matches = new List<Match>();
        try
        {
            var folder = Directory.Exists(path);
            var file = File.Exists(path);
            var directory = kind == "dir" || (kind.Length == 0
                && (folder || (!file && (Path.EndsInDirectorySeparator(path) || !Path.HasExtension(path)))));
            var entered = new Match(Token(value, directory), !folder && !file);
            matches.Add(entered);
            var parent = folder && Path.EndsInDirectorySeparator(path) ? path : Path.GetDirectoryName(path);
            var prefix = folder && Path.EndsInDirectorySeparator(path) ? string.Empty : Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                foreach (var candidate in Directory.EnumerateFileSystemEntries(parent).Take(2048))
                {
                    if (!Path.GetFileName(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var isFolder = Directory.Exists(candidate);
                    if (isFolder || candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(new Match(Token(candidate, isFolder), false));
                    }
                }
            }

            var ordered = matches.DistinctBy(match => match.Token, StringComparer.OrdinalIgnoreCase)
                .OrderBy(match => IsExecutable(match.Token) ? 0 : 1)
                .ThenBy(match => match.Missing)
                .ThenBy(match => string.Equals(match.Token, entered.Token, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(match => match.Token, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, limit)).ToList();
            if (!ordered.Any(match => string.Equals(match.Token, entered.Token, StringComparison.OrdinalIgnoreCase)))
            {
                ordered[^1] = entered;
            }

            return ordered;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            System.Diagnostics.Trace.TraceWarning("{0}", ex);
            return matches.Take(1).ToList();
        }
    }

    internal static bool IsExecutable(string token) => token.StartsWith("app:path=", StringComparison.OrdinalIgnoreCase)
        && token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static string Token(string path, bool directory) => AppPathToken.NormalizeAppRule(
        $"app:{(directory ? "dir" : "path")}={(directory ? Path.TrimEndingDirectorySeparator(path) : path)}");
}
