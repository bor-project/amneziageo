namespace AmneziaGeo.Geo;

/// <summary>
/// Reads and rewrites the AllowedIPs, DNS, Endpoint and MTU of a wg-quick config.
/// </summary>
public static class WgConfigEditor
{
    /// <summary>
    /// Returns the AllowedIPs entries declared in the config.
    /// </summary>
    public static IReadOnlyList<string> GetAllowedIps(string config)
    {
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("AllowedIPs", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed[(trimmed.IndexOf('=') + 1)..];
                return [.. value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
            }
        }

        return [];
    }

    /// <summary>
    /// Returns the interface Address entries declared in the config (e.g. 10.0.0.2/32).
    /// </summary>
    public static IReadOnlyList<string> GetAddresses(string config)
    {
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Address", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed[(trimmed.IndexOf('=') + 1)..];
                return [.. value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
            }
        }

        return [];
    }

    /// <summary>
    /// Returns the config with IPv6 entries removed from the interface Address line, keeping IPv4 only, so the
    /// adapter never receives a dangling IPv6 address on a v4-only tunnel. If no IPv4 entry remains (a v6-only
    /// config, for which an IPv4-only tunnel is meaningless), the Address line is left unchanged.
    /// </summary>
    public static string StripIpv6Addresses(string config)
    {
        var kept = new List<string>();
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Address", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed[(trimmed.IndexOf('=') + 1)..];
                var v4 = new List<string>();
                foreach (var entry in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!entry.Contains(':'))
                    {
                        v4.Add(entry);
                    }
                }

                if (v4.Count > 0)
                {
                    kept.Add($"Address = {string.Join(", ", v4)}");
                    continue;
                }
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// Returns the DNS servers declared in the config.
    /// </summary>
    public static IReadOnlyList<string> GetDns(string config)
    {
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("DNS", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed[(trimmed.IndexOf('=') + 1)..];
                return [.. value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
            }
        }

        return [];
    }

    /// <summary>
    /// Returns the raw peer Endpoint value (e.g. "vpn.example.com:9080"), or null when absent.
    /// </summary>
    public static string? GetEndpoint(string config)
    {
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Endpoint", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the config with its peer Endpoint replaced by the given value.
    /// </summary>
    public static string SetEndpoint(string config, string endpoint)
    {
        var kept = new List<string>();
        foreach (var line in config.Split('\n'))
        {
            if (line.Trim().StartsWith("Endpoint", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add($"Endpoint = {endpoint}");
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// Returns the peer public key declared in the config.
    /// </summary>
    public static string? GetPeerPublicKey(string config)
    {
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("PublicKey", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the config with its AllowedIPs replaced by the given entries.
    /// </summary>
    public static string ApplyAllowedIps(string config, IReadOnlyList<string> allowedIps)
    {
        var kept = new List<string>();
        var inserted = false;
        foreach (var line in config.Split('\n'))
        {
            if (line.Trim().StartsWith("AllowedIPs", StringComparison.OrdinalIgnoreCase))
            {
                if (allowedIps.Count > 0 && !inserted)
                {
                    kept.Add($"AllowedIPs = {string.Join(", ", allowedIps)}");
                    inserted = true;
                }

                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// Returns the config with its DNS lines removed.
    /// </summary>
    public static string RemoveDns(string config)
    {
        var kept = new List<string>();
        foreach (var line in config.Split('\n'))
        {
            if (!line.Trim().StartsWith("DNS", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(line);
            }
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// Returns the config with its DNS set to the given servers.
    /// </summary>
    public static string SetDns(string config, IReadOnlyList<string> servers)
    {
        if (servers.Count == 0)
        {
            return config;
        }

        var kept = new List<string>();
        foreach (var line in config.Split('\n'))
        {
            if (line.Trim().StartsWith("DNS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            kept.Add(line);
            if (line.Trim().Equals("[Interface]", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add($"DNS = {string.Join(", ", servers)}");
            }
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// Returns the [Interface] MTU declared in the config, or 0 when absent or unparseable.
    /// </summary>
    public static int GetMtu(string config)
    {
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("MTU", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed[(trimmed.IndexOf('=') + 1)..].Trim();
                return int.TryParse(value, out var mtu) ? mtu : 0;
            }
        }

        return 0;
    }
    /// <summary>
    /// Returns the config with its [Interface] MTU set to the given value.
    /// </summary>
    public static string SetMtu(string config, int mtu)
    {
        var kept = new List<string>();
        foreach (var line in config.Split('\n'))
        {
            if (line.Trim().StartsWith("MTU", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            kept.Add(line);
            if (line.Trim().Equals("[Interface]", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add($"MTU = {mtu}");
            }
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// Returns the config with a PersistentKeepalive added under [Peer] when none is present. An existing
    /// keepalive is left untouched so a server-specified interval wins over the injected default.
    /// </summary>
    public static string EnsurePersistentKeepalive(string config, int seconds)
    {
        foreach (var line in config.Split('\n'))
        {
            if (line.Trim().StartsWith("PersistentKeepalive", StringComparison.OrdinalIgnoreCase))
            {
                return config;
            }
        }

        var kept = new List<string>();
        foreach (var line in config.Split('\n'))
        {
            kept.Add(line);
            if (line.Trim().Equals("[Peer]", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add($"PersistentKeepalive = {seconds}");
            }
        }

        return string.Join('\n', kept);
    }
}
