using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Converts between a wg-quick .conf and an Amnezia vpn:// share link.
/// </summary>
internal static class VpnLinkCodec
{
    private const string _scheme = "vpn://";
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
            return new Imported(text, null);
        }

        if (text.StartsWith(_scheme, StringComparison.OrdinalIgnoreCase))
        {
            text = text[_scheme.Length..].Trim();
        }

        // vpn:// payload or a bare base64 blob.
        var bytes = TryBase64UrlDecode(text);
        if (bytes is not null)
        {
            var json = Encoding.UTF8.GetString(TryQUncompress(bytes) ?? bytes);
            var parsed = TryParseAmneziaJson(json);
            if (parsed is not null)
            {
                return parsed;
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

    private static bool LooksLikeConf(string text)
    {
        return text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
            && text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase);
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
