using System.Text;

namespace AmneziaGeo.Ipc;

/// <summary>
/// What a desktop entry says about the application it starts: the name it is shown under and the program behind
/// it, in the words an app rule is written in.
/// </summary>
public static class DesktopEntry
{
    private const string Group = "[Desktop Entry]";

    // A launcher every packaged application is started by; a rule on it names them all.
    private const string SharedLauncher = "flatpak";

    /// <summary>
    /// Reads one entry, or null when it hides itself, starts no program of its own, or starts it through the
    /// launcher shared by every packaged application. The language picks the name the interface runs in.
    /// </summary>
    public static (string Name, string Image)? Read(IReadOnlyList<string> lines, string language)
    {
        var fields = Fields(lines);
        if (!fields.TryGetValue("Type", out var kind) || !string.Equals(kind, "Application", StringComparison.Ordinal))
        {
            return null;
        }

        if (fields.TryGetValue("NoDisplay", out var hidden) && string.Equals(hidden, "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = Named(fields, language);
        if (name.Length == 0 || Image(fields) is not { } image)
        {
            return null;
        }

        return string.Equals(Path.GetFileName(image), SharedLauncher, StringComparison.Ordinal) ? null : (name, image);
    }

    // The keys of the entry's own group; a group after it describes another action of the same application.
    private static Dictionary<string, string> Fields(IReadOnlyList<string> lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var inside = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                if (inside)
                {
                    break;
                }

                inside = string.Equals(line, Group, StringComparison.Ordinal);
                continue;
            }

            var eq = line.IndexOf('=');
            if (!inside || eq <= 0 || line.StartsWith('#'))
            {
                continue;
            }

            fields[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        return fields;
    }

    // The name in the language of the interface, else the one the entry carries plain.
    private static string Named(Dictionary<string, string> fields, string language)
    {
        if (language.Length > 0 && fields.TryGetValue($"Name[{language}]", out var localized) && localized.Length > 0)
        {
            return localized;
        }

        return fields.TryGetValue("Name", out var name) ? name : string.Empty;
    }

    // The program the entry starts: what TryExec names, else the head of Exec.
    private static string? Image(Dictionary<string, string> fields)
    {
        if (fields.TryGetValue("TryExec", out var tried) && Program(tried) is { } named)
        {
            return named;
        }

        return fields.TryGetValue("Exec", out var exec) ? Program(Head(exec)) : null;
    }

    // The command's first word, past the environment it is started with.
    private static string Head(string command)
    {
        foreach (var word in Words(command))
        {
            if (!string.Equals(word, "env", StringComparison.Ordinal) && !word.Contains('='))
            {
                return word;
            }
        }

        return string.Empty;
    }

    // The words of a command line, keeping a quoted path with spaces whole.
    private static IEnumerable<string> Words(string command)
    {
        var word = new StringBuilder();
        var quoted = false;
        foreach (var c in command)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (c == ' ' && !quoted)
            {
                if (word.Length > 0)
                {
                    yield return word.ToString();
                    word.Clear();
                }

                continue;
            }

            word.Append(c);
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    // Where a named program stands: a whole path as written, a bare name looked up in the path of the machine.
    private static string? Program(string value)
    {
        var name = value.Trim().Trim('"');
        if (name.Length == 0)
        {
            return null;
        }

        if (name.StartsWith('/'))
        {
            return name;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = directory.Trim().TrimEnd('/') + "/" + name;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
