using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AmneziaGeo.Decl;

/// <summary>
/// Converts between a wg-quick .conf and an Amnezia vpn:// share link.
/// </summary>
public static class VpnLinkCodec
{
    private const string _scheme = "vpn://";
    private const string _wgScheme = "wireguard://";
    private const short _qrMagic = 1984;

    /// <summary>
    /// Result of a successful import.
    /// </summary>
    public sealed record Imported(string ConfText, string? Name);

    /// <summary>
    /// Builds an Amnezia vpn:// link from a wg-quick config.
    /// </summary>
    public static string Encode(string confText, string? name)
    {
        var (host, port) = ParseEndpoint(confText);
        var lastConfig = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["config"] = confText,
            ["hostName"] = host,
            ["port"] = port,
        });

        var root = new Dictionary<string, object?>
        {
            ["containers"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["container"] = "amnezia-awg",
                    ["awg"] = new Dictionary<string, object?>
                    {
                        ["last_config"] = lastConfig,
                        ["isThirdPartyConfig"] = true,
                        ["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["transport_proto"] = "udp",
                    },
                },
            },
            ["defaultContainer"] = "amnezia-awg",
            ["description"] = string.IsNullOrWhiteSpace(name) ? host : name,
            ["hostName"] = host,
        };

        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(root));
        return _scheme + Base64UrlEncode(QCompress(json));
    }

    // Keys of the AmneziaWG obfuscation, which no wireguard:// link can carry.
    private static readonly HashSet<string> _amneziaKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4",
        "I1", "I2", "I3", "I4", "I5", "HeaderProtectionKey", "ContentPaddingAddition",
        "RekeyAfterTime", "RekeyTimeout", "RejectAfterTime", "KeepaliveTimeout",
        "MaxHandshakeAttempts", "RandomTrailers", "DisableCookies",
    };

    /// <summary>
    /// Builds the wireguard:// share link x-ui and Hiddify read. Returns null for a config the link cannot
    /// carry: one without a private key or an endpoint, and any config with AmneziaWG obfuscation.
    /// </summary>
    public static string? EncodeWireguard(string confText, string? name)
    {
        var iface = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;

        foreach (var rawLine in confText.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (_amneziaKeys.Contains(key))
            {
                return null;
            }

            if (section.Equals("Interface", StringComparison.OrdinalIgnoreCase))
            {
                iface.TryAdd(key, value);
            }
            else if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase))
            {
                peer.TryAdd(key, value);
            }
        }

        if (!iface.TryGetValue("PrivateKey", out var privateKey) || !peer.TryGetValue("Endpoint", out var endpoint))
        {
            return null;
        }

        // Alphabetical order and the same escaping the panel writes, so a link of ours matches one of its own.
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Carry(query, "address", iface, "Address");
        Carry(query, "dns", iface, "DNS");
        Carry(query, "keepalive", peer, "PersistentKeepalive");
        Carry(query, "mtu", iface, "MTU");
        Carry(query, "presharedkey", peer, "PresharedKey");
        Carry(query, "publickey", peer, "PublicKey");

        var link = new StringBuilder(_wgScheme);
        link.Append(Uri.EscapeDataString(privateKey)).Append('@').Append(endpoint);

        var separator = '?';
        foreach (var pair in query)
        {
            link.Append(separator).Append(pair.Key).Append('=').Append(Uri.EscapeDataString(pair.Value));
            separator = '&';
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            link.Append('#').Append(Uri.EscapeDataString(name!.Trim()));
        }

        return link.ToString();
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        return hash < 0 ? line : line[..hash];
    }

    private static void Carry(SortedDictionary<string, string> query, string param, IReadOnlyDictionary<string, string> from, string key)
    {
        if (from.TryGetValue(key, out var value))
        {
            query[param] = value;
        }
    }

    /// <summary>
    /// Parses a pasted/loaded string into a wg-quick config.
    /// </summary>
    public static Imported? TryDecode(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var text = input.Trim();
        if (LooksLikeConf(text))
        {
            return new Imported(text, Remark(text));
        }

        if (text.StartsWith(_wgScheme, StringComparison.OrdinalIgnoreCase))
        {
            return TryDecodeWireguardLink(text);
        }

        if (text.StartsWith(_scheme, StringComparison.OrdinalIgnoreCase))
        {
            text = text[_scheme.Length..].Trim();
        }

        // vpn:// payload or a bare base64 blob.
        var bytes = TryBase64UrlDecode(text);
        if (bytes is not null)
        {
            var payload = Encoding.UTF8.GetString(TryQUncompress(bytes) ?? bytes);
            var parsed = TryParseAmneziaJson(payload);
            if (parsed is not null)
            {
                return parsed;
            }

            // Amnezia wraps the config into a JSON document, x-ui packs the config text itself.
            if (LooksLikeConf(payload))
            {
                return new Imported(payload, Remark(payload));
            }
        }

        // Bare JSON document.
        if (text.StartsWith('{'))
        {
            return TryParseAmneziaJson(text);
        }

        return null;
    }

    /// <summary>
    /// Parses text scanned from a QR.
    /// </summary>
    public static Imported? TryDecodeQr(string qrText)
    {
        if (string.IsNullOrWhiteSpace(qrText))
        {
            return null;
        }

        var text = qrText.Trim();
        if (text.StartsWith(_scheme, StringComparison.OrdinalIgnoreCase) || LooksLikeConf(text))
        {
            return TryDecode(text);
        }

        // Amnezia chunk wrapper.
        var bytes = TryBase64UrlDecode(text);
        if (bytes is { Length: >= 8 } && (short)((bytes[0] << 8) | bytes[1]) == _qrMagic)
        {
            int count = bytes[2];
            int len = (bytes[4] << 24) | (bytes[5] << 16) | (bytes[6] << 8) | bytes[7];
            if (count == 1 && len > 0 && 8 + len <= bytes.Length)
            {
                var payload = new byte[len];
                Array.Copy(bytes, 8, payload, 0, len);
                var json = Encoding.UTF8.GetString(TryQUncompress(payload) ?? payload);
                return TryParseAmneziaJson(json);
            }

            // Multi-chunk QR is not supported.
            return null;
        }

        return TryDecode(text);
    }

    /// <summary>
    /// Returns the peer host without its port, or null when the config carries no endpoint.
    /// </summary>
    public static string? HostName(string confText)
    {
        var (host, _) = ParseEndpoint(confText);
        return string.IsNullOrWhiteSpace(host) ? null : host;
    }

    /// <summary>
    /// Whether the text is a wg-quick configuration.
    /// </summary>
    public static bool LooksLikeConf(string text)
    {
        return text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
            && text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase);
    }

    // The wireguard:// share link x-ui and Hiddify write: the private key is the userinfo, the rest rides in the query.
    private static Imported? TryDecodeWireguardLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Port < 0)
        {
            return null;
        }

        var privateKey = Uri.UnescapeDataString(uri.UserInfo);
        if (privateKey.Length == 0)
        {
            return null;
        }

        var query = ParseQuery(uri.Query);
        var lines = new List<string> { "[Interface]", $"PrivateKey = {privateKey}" };
        AppendParam(lines, query, "address", "Address");
        AppendParam(lines, query, "dns", "DNS");
        AppendParam(lines, query, "mtu", "MTU");

        lines.Add(string.Empty);
        lines.Add("[Peer]");
        AppendParam(lines, query, "publickey", "PublicKey");
        AppendParam(lines, query, "presharedkey", "PresharedKey");
        lines.Add("AllowedIPs = 0.0.0.0/0, ::/0");
        lines.Add($"Endpoint = {Endpoint(uri)}");
        AppendParam(lines, query, "keepalive", "PersistentKeepalive");

        var name = uri.Fragment.Length > 1 ? Uri.UnescapeDataString(uri.Fragment[1..]) : string.Empty;
        return new Imported(string.Join('\n', lines), name.Length == 0 ? null : SafeName(name));
    }

    private static void AppendParam(List<string> lines, IReadOnlyDictionary<string, string> query, string param, string key)
    {
        if (query.TryGetValue(param, out var value))
        {
            lines.Add($"{key} = {value}");
        }
    }

    private static string Endpoint(Uri uri)
    {
        var host = uri.Host;
        if (host.Contains(':') && !host.StartsWith('['))
        {
            host = $"[{host}]";
        }

        return $"{host}:{uri.Port}";
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' ')).Trim();
            if (value.Length > 0)
            {
                result[pair[..equals]] = value;
            }
        }

        return result;
    }

    // The name x-ui writes as a comment ahead of [Peer].
    private static string? Remark(string confText)
    {
        var remark = default(string);
        foreach (var rawLine in confText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("[Peer]", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (line.StartsWith('#'))
            {
                remark = line[1..].Trim();
            }
        }

        return string.IsNullOrWhiteSpace(remark) ? null : SafeName(remark!);
    }

    // A config name goes on to name the tunnel adapter, which takes neither spaces nor brackets.
    private static string? SafeName(string text)
    {
        var name = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        }

        var collapsed = name.ToString();
        while (collapsed.Contains("--", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("--", "-", StringComparison.Ordinal);
        }

        collapsed = collapsed.Trim('-', '.');
        return collapsed.Length == 0 ? null : collapsed;
    }

    private static Imported? TryParseAmneziaJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var name = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;

            if (root.TryGetProperty("containers", out var containers) && containers.ValueKind == JsonValueKind.Array)
            {
                foreach (var container in containers.EnumerateArray())
                {
                    if ((container.TryGetProperty("awg", out var proto) || container.TryGetProperty("wireguard", out proto))
                        && proto.TryGetProperty("last_config", out var lc) && lc.ValueKind == JsonValueKind.String)
                    {
                        using var inner = JsonDocument.Parse(lc.GetString()!);
                        if (inner.RootElement.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.String)
                        {
                            return new Imported(cfg.GetString()!, name);
                        }
                    }
                }
            }

            // The document may itself be a last_config object.
            if (root.TryGetProperty("config", out var direct) && direct.ValueKind == JsonValueKind.String)
            {
                return new Imported(direct.GetString()!, name);
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static (string Host, int Port) ParseEndpoint(string conf)
    {
        foreach (var raw in conf.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Endpoint", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var value = line[(eq + 1)..].Trim();
            var colon = value.LastIndexOf(':');
            if (colon > 0 && int.TryParse(value[(colon + 1)..], out var port))
            {
                return (value[..colon], port);
            }
        }

        return (string.Empty, 51820);
    }

    private static byte[] QCompress(byte[] data)
    {
        using var output = new MemoryStream();
        output.WriteByte((byte)((data.Length >> 24) & 0xFF));
        output.WriteByte((byte)((data.Length >> 16) & 0xFF));
        output.WriteByte((byte)((data.Length >> 8) & 0xFF));
        output.WriteByte((byte)(data.Length & 0xFF));
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static byte[]? TryQUncompress(byte[] data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        try
        {
            using var input = new MemoryStream(data, 4, data.Length - 4);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static byte[]? TryBase64UrlDecode(string text)
    {
        var b64 = text.Replace('-', '+').Replace('_', '/');
        b64 += new string('=', (4 - (b64.Length % 4)) % 4);
        try
        {
            return Convert.FromBase64String(b64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
