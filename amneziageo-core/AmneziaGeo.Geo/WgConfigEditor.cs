namespace AmneziaGeo.Geo;

/// <summary>
/// Reads and rewrites the AllowedIPs and DNS of a wg-quick config.
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
}
