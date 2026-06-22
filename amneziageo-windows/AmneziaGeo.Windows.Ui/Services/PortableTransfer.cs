using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Encodes/decodes a config's WebSocket transport settings and a routing list as small, human-readable,
/// line-based text blobs, so they can be shared the same way a config is (copy / save to file / paste).
/// The formats are intentionally plain so a recipient can also read or hand-edit them.
/// </summary>
internal static class PortableTransfer
{
    private const string WebSocketHeader = "#ageo-websocket v1";
    private const string RoutingHeader = "#ageo-routing v1";

    /// <summary>
    /// Serialises a config's WebSocket transport. <paramref name="host"/> is the stored address (a bare
    /// host or a full wss://[user:pass@]host:port[/token] URL carrying auth), so auth travels with it.
    /// </summary>
    public static string EncodeWebSocket(bool enabled, int port, string host)
    {
        var sb = new StringBuilder();
        sb.Append(WebSocketHeader).Append('\n');
        sb.Append("enabled: ").Append(enabled ? "true" : "false").Append('\n');
        sb.Append("port: ").Append(port.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("host: ").Append(host ?? string.Empty).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Parses a WebSocket transport blob produced by <see cref="EncodeWebSocket"/>. Returns false when the
    /// text is not a recognisable websocket blob.
    /// </summary>
    public static bool TryDecodeWebSocket(string? text, out bool enabled, out int port, out string host)
    {
        enabled = false;
        port = 443;
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("#ageo-websocket", System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sawPort = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "enabled":
                    enabled = value.Equals("true", System.StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("on", System.StringComparison.OrdinalIgnoreCase);
                    break;
                case "port":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535)
                    {
                        port = p;
                        sawPort = true;
                    }

                    break;
                case "host":
                    host = value;
                    break;
            }
        }

        // A valid blob carries at least a port or a host (an all-defaults blob is meaningless).
        return sawPort || host.Length > 0;
    }

    /// <summary>
    /// Serialises a routing list: a name header then one rule token per line (geosite:openai, domain:..,
    /// cidr:..). Comment lines start with '#'.
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
    /// Parses a routing-list blob produced by <see cref="EncodeRouting"/>. The name comes from the
    /// "#name:" header; every non-comment line is a rule. Returns false when the text is not recognisable.
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
                // "#name: <name>" is the only meaningful comment; other comments are ignored.
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
