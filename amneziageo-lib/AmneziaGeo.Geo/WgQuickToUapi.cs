using System.Text;

namespace AmneziaGeo.Geo;

/// <summary>
/// Converts a wg-quick config into the amneziawg-go UAPI "set" payload.
/// </summary>
public static class WgQuickToUapi
{
    // Interface-level AmneziaWG obfuscation keys the amneziawg-go device accepts, mapped to their UAPI tokens.
    private static readonly IReadOnlyDictionary<string, string> AwgKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Jc"] = "jc",
        ["Jmin"] = "jmin",
        ["Jmax"] = "jmax",
        ["S1"] = "s1",
        ["S2"] = "s2",
        ["S3"] = "s3",
        ["S4"] = "s4",
        ["H1"] = "h1",
        ["H2"] = "h2",
        ["H3"] = "h3",
        ["H4"] = "h4",
        ["I1"] = "i1",
        ["I2"] = "i2",
        ["I3"] = "i3",
        ["I4"] = "i4",
        ["I5"] = "i5",
    };

    /// <summary>
    /// Returns the UAPI set payload for the config, or null when the interface private key is missing or unparseable.
    /// The peer Endpoint must already be a literal IP:port; the engine does not resolve hostnames.
    /// </summary>
    public static string? Convert(string config)
    {
        var deviceLines = new List<string>();
        var peers = new List<List<string>>();
        var current = default(List<string>);
        var section = string.Empty;
        var havePrivateKey = false;

        foreach (var rawLine in config.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase))
                {
                    current = new List<string>();
                    peers.Add(current);
                }

                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (section.Equals("Interface", StringComparison.OrdinalIgnoreCase))
            {
                AppendInterfaceLine(deviceLines, key, value, ref havePrivateKey);
            }
            else if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase) && current is not null)
            {
                AppendPeerLine(current, key, value);
            }
        }

        if (!havePrivateKey)
        {
            return null;
        }

        var uapi = new StringBuilder();
        foreach (var deviceLine in deviceLines)
        {
            uapi.Append(deviceLine).Append('\n');
        }

        uapi.Append("replace_peers=true\n");
        foreach (var peer in peers)
        {
            if (peer.Count == 0 || !peer[0].StartsWith("public_key=", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var peerLine in peer)
            {
                uapi.Append(peerLine).Append('\n');
            }
        }

        uapi.Append('\n');
        return uapi.ToString();
    }

    private static void AppendInterfaceLine(List<string> lines, string key, string value, ref bool havePrivateKey)
    {
        if (key.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase))
        {
            var hex = KeyToHex(value);
            if (hex is null)
            {
                return;
            }

            lines.Add($"private_key={hex}");
            havePrivateKey = true;
        }
        else if (key.Equals("ListenPort", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"listen_port={value}");
        }
        else if (AwgKeys.TryGetValue(key, out var token))
        {
            lines.Add($"{token}={value}");
        }
    }

    private static void AppendPeerLine(List<string> peer, string key, string value)
    {
        if (key.Equals("PublicKey", StringComparison.OrdinalIgnoreCase))
        {
            var hex = KeyToHex(value);
            if (hex is not null)
            {
                peer.Insert(0, $"public_key={hex}");
            }
        }
        else if (key.Equals("PresharedKey", StringComparison.OrdinalIgnoreCase))
        {
            var hex = KeyToHex(value);
            if (hex is not null)
            {
                peer.Add($"preshared_key={hex}");
            }
        }
        else if (key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
        {
            peer.Add($"endpoint={value}");
        }
        else if (key.Equals("PersistentKeepalive", StringComparison.OrdinalIgnoreCase))
        {
            peer.Add($"persistent_keepalive_interval={value}");
        }
        else if (key.Equals("AllowedIPs", StringComparison.OrdinalIgnoreCase))
        {
            peer.Add("replace_allowed_ips=true");
            foreach (var cidr in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                peer.Add($"allowed_ip={cidr}");
            }
        }
    }

    private static string? KeyToHex(string base64)
    {
        try
        {
            var bytes = System.Convert.FromBase64String(base64);
            return bytes.Length == 32 ? System.Convert.ToHexStringLower(bytes) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        return hash < 0 ? line : line[..hash];
    }
}
