using System.Text.Json;
using System.Text.Json.Nodes;

namespace AmneziaGeo.Cli;

/// <summary>
/// Hides the secrets of a console answer that a broadcast reply would hand to the system journal.
/// </summary>
public static class CliSecrets
{
    private const string Hidden = "***";

    // The names an answer carries a secret under, whatever casing the serializer wrote them in.
    private static readonly string[] _names =
        ["credential", "credentials", "password", "privatekey", "presharedkey", "proxycredentials"];

    private static readonly string[] _lines = ["PrivateKey", "PresharedKey"];

    // The schemes an import link travels under.
    private static readonly string[] _links = ["vpn://", "amneziageo://"];

    // The shape the console prints JSON in.
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    /// <summary>
    /// Returns the answer with its secrets hidden: a JSON answer keeps its structure, a config text keeps
    /// its lines, and an import link keeps nothing but its scheme.
    /// </summary>
    public static string Mask(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var head = text.TrimStart();
        if (head.StartsWith('{') || head.StartsWith('['))
        {
            try
            {
                if (JsonNode.Parse(text) is { } node)
                {
                    Hide(node);
                    return node.ToJsonString(_json);
                }
            }
            catch (JsonException)
            {
            }
        }

        return HideLines(text);
    }

    private static void Hide(JsonNode node)
    {
        if (node is JsonObject value)
        {
            foreach (var name in value.Select(item => item.Key).ToArray())
            {
                if (_names.Contains(name.Replace("_", string.Empty, StringComparison.Ordinal), StringComparer.OrdinalIgnoreCase))
                {
                    value[name] = Hidden;
                }
                else if (value[name] is { } child)
                {
                    Hide(child);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.ToArray())
            {
                if (child is not null)
                {
                    Hide(child);
                }
            }
        }
    }

    private static string HideLines(string text)
    {
        var lines = text.Split('\n');
        var changed = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var hidden = HideLine(lines[index]);
            if (!string.Equals(hidden, lines[index], StringComparison.Ordinal))
            {
                lines[index] = hidden;
                changed = true;
            }
        }

        return changed ? string.Join("\n", lines) : text;
    }

    // A line with a key of a WireGuard config, or a whole import link.
    private static string HideLine(string line)
    {
        var trimmed = line.TrimStart();
        foreach (var scheme in _links)
        {
            if (trimmed.StartsWith(scheme, StringComparison.Ordinal))
            {
                return line[..(line.Length - trimmed.Length + scheme.Length)] + Hidden;
            }
        }

        var equals = line.IndexOf('=');
        if (equals <= 0)
        {
            return line;
        }

        var name = line[..equals].Trim();
        if (!_lines.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return line;
        }

        var value = equals + 1;
        while (value < line.Length && line[value] == ' ')
        {
            value++;
        }

        return line[..value] + Hidden;
    }
}
