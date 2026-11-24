using System.Net;
using System.Net.Sockets;

namespace AmneziaGeo.Decl;

public static class Ipv6Plan
{
    public static bool Available(string? config, bool? supported = null)
    {
        if (!(supported ?? Socket.OSSupportsIPv6))
        {
            return false;
        }

        var routes = Entries(config, "Peer", "AllowedIPs");
        return Entries(config, "Interface", "Address").Any(Unicast)
            && (routes.Count == 0 || routes.Any(Range));
    }

    public static bool Enabled(ConfigTransport? transport, string? config) => Ipv6Modes.Of(transport) switch
    {
        Ipv6Mode.On => true,
        Ipv6Mode.Off => false,
        _ => Available(config),
    };

    public static string ForTunnel(string config, bool enabled)
    {
        if (enabled)
        {
            return config;
        }

        return string.Join('\n', config.Split('\n').Select(line =>
        {
            var comment = line.IndexOf('#');
            var text = comment < 0 ? line : line[..comment];
            var separator = text.IndexOf('=');
            if (separator < 0 || text[..separator].Trim().ToLowerInvariant() is not ("address" or "allowedips" or "dns"))
            {
                return line;
            }

            var kept = text[(separator + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(entry => !entry.Contains(':', StringComparison.Ordinal));
            return $"{text[..separator].Trim()} = {string.Join(", ", kept)}";
        }));
    }

    private static bool Unicast(string entry) => Address(entry) is { } address
        && !address.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(address)
        && !address.IsIPv6Multicast && !address.IsIPv6LinkLocal;

    private static bool Range(string entry) => Address(entry) is not null;

    private static IReadOnlyList<string> Entries(string? config, string section, string key)
    {
        var current = string.Empty;
        var entries = new List<string>();
        foreach (var line in (config ?? string.Empty).Split('\n'))
        {
            var text = line.Split('#', 2)[0].Trim();
            if (text.StartsWith('[') && text.EndsWith(']'))
            {
                current = text[1..^1];
                continue;
            }

            var separator = text.IndexOf('=');
            if (current.Equals(section, StringComparison.OrdinalIgnoreCase) && separator > 0
                && text[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                entries.AddRange(text[(separator + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            }
        }

        return entries;
    }

    private static IPAddress? Address(string entry)
    {
        var parts = entry.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6
            || parts.Length == 2 && (!int.TryParse(parts[1], out var prefix) || prefix is < 0 or > 128))
        {
            return null;
        }

        return address;
    }
}
