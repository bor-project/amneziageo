using System.Collections.Generic;
using System.Text;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Encodes/decodes routing lists as plain text blobs.
/// </summary>
internal static class PortableTransfer
{
    private const string RoutingHeader = "#ageo-routing v1";
    private const string NameTag = "#name:";
    private const string AllUdpTag = "#all-udp:";
    private const string GlobalProxyTag = "#global-proxy:";

    /// <summary>
    /// Traffic options of a routing list, carried alongside its rules.
    /// </summary>
    public sealed record RoutingOptions(bool AllUdp, bool UseGlobalProxy);

    /// <summary>
    /// Serialises a routing list.
    /// </summary>
    public static string EncodeRouting(string name, IReadOnlyList<string> rules, RoutingOptions? options = null)
    {
        var sb = new StringBuilder();
        sb.Append(RoutingHeader).Append('\n');
        sb.Append(NameTag).Append(' ').Append(name ?? string.Empty).Append('\n');
        if (options is not null)
        {
            sb.Append(AllUdpTag).Append(' ').Append(options.AllUdp ? '1' : '0').Append('\n');
            sb.Append(GlobalProxyTag).Append(' ').Append(options.UseGlobalProxy ? '1' : '0').Append('\n');
        }

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
        return TryDecodeRouting(text, out name, out rules, out _);
    }

    /// <summary>
    /// Parses a routing-list blob together with its traffic options; the options are null when the blob carries none.
    /// </summary>
    public static bool TryDecodeRouting(string? text, out string name, out IReadOnlyList<string> rules, out RoutingOptions? options)
    {
        name = string.Empty;
        options = null;
        var list = new List<string>();
        var seen = new HashSet<string>();
        rules = list;
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("#ageo-routing", System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var allUdp = false;
        var globalProxy = false;
        var hasOptions = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (line.StartsWith(NameTag, System.StringComparison.OrdinalIgnoreCase))
                {
                    name = line[NameTag.Length..].Trim();
                }
                else if (line.StartsWith(AllUdpTag, System.StringComparison.OrdinalIgnoreCase))
                {
                    allUdp = IsOn(line[AllUdpTag.Length..]);
                    hasOptions = true;
                }
                else if (line.StartsWith(GlobalProxyTag, System.StringComparison.OrdinalIgnoreCase))
                {
                    globalProxy = IsOn(line[GlobalProxyTag.Length..]);
                    hasOptions = true;
                }

                continue;
            }

            if (seen.Add(line))
            {
                list.Add(line);
            }
        }

        options = hasOptions ? new RoutingOptions(allUdp, globalProxy) : null;
        return true;
    }

    private static bool IsOn(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();
        return trimmed is "1" or "on" or "true" or "yes";
    }
}
