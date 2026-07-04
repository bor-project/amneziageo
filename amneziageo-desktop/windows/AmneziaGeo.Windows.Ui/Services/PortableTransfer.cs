using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Encodes/decodes WebSocket transport and routing lists as plain text blobs.
/// </summary>
internal static class PortableTransfer
{
    private const string WebSocketHeader = "#ageo-websocket v1";
    private const string RoutingHeader = "#ageo-routing v1";

    /// <summary>
    /// Serialises a WebSocket transport.
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
    /// Parses a WebSocket transport blob.
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

        // At least a port or a host.
        return sawPort || host.Length > 0;
    }

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
