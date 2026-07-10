using System.Collections.Generic;
using System.Text;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Encodes/decodes routing lists as plain text blobs.
/// </summary>
internal static class PortableTransfer
{
    private const string RoutingHeader = "#ageo-routing v1";

    /// <summary>
    /// Serialises a routing list.
    /// </summary>
    public static string EncodeRouting(string name, IReadOnlyList<string> rules)
    {
        var sb = new StringBuilder();
        sb.Append(RoutingHeader).Append('\n');
        sb.Append("#name: ").Append(name ?? string.Empty).Append('\n');
        foreach (var rule in rules)
        {
            var trimmed = rule?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                sb.Append(trimmed).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses a routing-list blob.
    /// </summary>
    public static bool TryDecodeRouting(string? text, out string name, out IReadOnlyList<string> rules)
    {
        name = string.Empty;
        var list = new List<string>();
        rules = list;
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("#ageo-routing", System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                // Only #name: is read.
                const string nameTag = "#name:";
                if (line.StartsWith(nameTag, System.StringComparison.OrdinalIgnoreCase))
                {
                    name = line[nameTag.Length..].Trim();
                }

                continue;
            }

            if (!list.Contains(line))
            {
                list.Add(line);
            }
        }

        return true;
    }
}
