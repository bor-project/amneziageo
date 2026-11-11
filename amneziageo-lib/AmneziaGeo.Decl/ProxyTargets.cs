using System.Globalization;
using System.Net;

namespace AmneziaGeo.Decl;

/// <summary>
/// Names an allowed UDP destination.
/// </summary>
public sealed record ProxyDestination(string Host, int Port);

/// <summary>
/// Limits the UDP destinations of a proxy.
/// </summary>
public sealed record ProxyTargets(bool Restricted, IReadOnlyList<ProxyDestination> Destinations);

/// <summary>
/// Validates proxy destination policies.
/// </summary>
public static class ProxyTargetRules
{
    public const int MaximumDestinations = 64;

    /// <summary>
    /// Normalizes a destination host.
    /// </summary>
    public static string? NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var host = value.Trim();
        var bracketed = host.StartsWith('[') && host.EndsWith(']');
        if (bracketed)
        {
            host = host[1..^1];
        }

        if (host.Any(char.IsControl) || host.IndexOfAny(['%', '/', '\\', '@', '*', '?', '#']) >= 0)
        {
            return null;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        }

        if (bracketed)
        {
            return null;
        }

        host = host.EndsWith('.') ? host[..^1] : host;
        try
        {
            var ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(host.ToLowerInvariant()).ToLowerInvariant();
            var labels = ascii.Split('.');
            return ascii.Length is > 0 and <= 253 && !labels.Any(label => label.Length is 0 or > 63
                || label[0] == '-' || label[^1] == '-'
                || label.Any(letter => !char.IsAsciiLetterOrDigit(letter) && letter != '-')) ? ascii : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Normalizes a destination policy.
    /// </summary>
    public static ProxyTargets? Normalize(ProxyTargets? policy)
    {
        if (policy is null)
        {
            return null;
        }

        if (policy.Destinations is null || policy.Destinations.Count > 1024)
        {
            throw new ArgumentException("The proxy destination policy is invalid.", nameof(policy));
        }

        var destinations = new List<ProxyDestination>();
        foreach (var destination in policy.Destinations)
        {
            if (destination is null || NormalizeHost(destination.Host) is not { } host || destination.Port is < 1 or > 65535)
            {
                throw new ArgumentException("The proxy destination is invalid.", nameof(policy));
            }

            destinations.Add(new ProxyDestination(host, destination.Port));
        }

        var normalized = destinations.Distinct().OrderBy(one => one.Host, StringComparer.Ordinal).ThenBy(one => one.Port).ToArray();
        if (normalized.Length > MaximumDestinations)
        {
            throw new ArgumentException("The proxy destination policy has too many destinations.", nameof(policy));
        }

        if (policy.Restricted && normalized.Length == 0)
        {
            throw new ArgumentException("The proxy destination policy is empty.", nameof(policy));
        }

        return policy with { Destinations = normalized };
    }

    /// <summary>
    /// Checks a destination policy.
    /// </summary>
    public static bool Valid(ProxyTargets? policy)
    {
        try
        {
            _ = Normalize(policy);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks the destination against the policy.
    /// </summary>
    public static bool Allows(ProxyTargets? policy, string? host, int port)
    {
        if (port is < 1 or > 65535 || !Valid(policy))
        {
            return false;
        }

        if (policy?.Restricted != true)
        {
            var previous = host?.Trim() ?? string.Empty;
            previous = previous.StartsWith('[') && previous.EndsWith(']') ? previous[1..^1] : previous;
            return !previous.Any(char.IsControl) && !previous.Any(char.IsWhiteSpace)
                && Uri.CheckHostName(previous) != UriHostNameType.Unknown;
        }

        if (NormalizeHost(host) is not { } normalized)
        {
            return false;
        }

        return policy.Destinations.Any(one => one.Port == port && NormalizeHost(one.Host) == normalized);
    }

    /// <summary>
    /// Compares destination policies.
    /// </summary>
    public static bool Same(ProxyTargets? first, ProxyTargets? second)
    {
        if (!Valid(first) || !Valid(second))
        {
            return false;
        }

        var left = Normalize(first);
        var right = Normalize(second);
        if (left?.Restricted != true && right?.Restricted != true)
        {
            return true;
        }

        return left?.Restricted == right?.Restricted && (left?.Destinations ?? []).SequenceEqual(right?.Destinations ?? []);
    }

    /// <summary>
    /// Formats a destination.
    /// </summary>
    public static string Format(ProxyDestination destination) =>
        string.Create(CultureInfo.InvariantCulture, $"{(destination.Host.Contains(':') ? $"[{destination.Host}]" : destination.Host)}:{destination.Port}");

    /// <summary>
    /// Reads destination rows.
    /// </summary>
    public static ProxyTargets Parse(string text, bool restricted)
    {
        var destinations = new List<ProxyDestination>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(line[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || line[..colon].Contains(':') && !(line.StartsWith('[') && line[colon - 1] == ']'))
            {
                throw new ArgumentException("The proxy destination row is invalid.", nameof(text));
            }

            destinations.Add(new ProxyDestination(line[..colon], port));
        }

        return Normalize(new ProxyTargets(restricted, destinations))!;
    }
}
