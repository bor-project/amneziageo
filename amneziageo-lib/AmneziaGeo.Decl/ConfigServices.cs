using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AmneziaGeo.Decl;

/// <summary>
/// Where the services of the server of a config answer and the keys the config proves itself to them with.
/// </summary>
/// <param name="Host">The host of the Endpoint, without brackets.</param>
/// <param name="Port">The TCP port the services answer on.</param>
/// <param name="PrivateKey">The private key of the config.</param>
/// <param name="ServerKey">The public key of the server.</param>
public sealed record ServiceTarget(string Host, int Port, string PrivateKey, string ServerKey)
{
    /// <summary>
    /// Returns the mark an answer of these services is kept under: the point and the keys, not the rest of the text.
    /// </summary>
    public string Mark() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{Host}\n{Port}\n{PrivateKey}\n{ServerKey}"))));

    /// <summary>
    /// Returns the address of the services, without the keys.
    /// </summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{(Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host)}:{Port}");
}

/// <summary>
/// Reads where the services of the server of a config answer.
/// </summary>
public static class ConfigServices
{
    /// <summary>
    /// The comment a server of ours names the TCP port of its services under, when it is not the port of the Endpoint.
    /// </summary>
    public const string Line = "AmneziaGeo Services";

    /// <summary>
    /// Returns the host of the Endpoint of a config text, without brackets; empty when it names none.
    /// </summary>
    public static string Host(string? text) => Split(Value(text, "Endpoint")).Host;

    /// <summary>
    /// Returns the TCP port the services answer on: the one the line names, else the port of the Endpoint; zero when
    /// neither holds.
    /// </summary>
    public static int Port(string? text) =>
        int.TryParse(Comment(text, Line), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : Split(Value(text, "Endpoint")).Port;

    /// <summary>
    /// Returns where the services of a config text answer and its keys; null when one of them is missing.
    /// </summary>
    public static ServiceTarget? Target(string? text)
    {
        var host = Host(text);
        var port = Port(text);
        var privateKey = Value(text, "PrivateKey");
        var serverKey = Value(text, "PublicKey");

        return host.Length > 0 && port > 0 && privateKey.Length > 0 && serverKey.Length > 0
            ? new ServiceTarget(host, port, privateKey, serverKey)
            : null;
    }

    // Reads the value of the first "Key = value" line under that key.
    private static string Value(string? text, string key)
    {
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            var equals = trimmed.IndexOf('=');
            if (!trimmed.StartsWith('#') && equals > 0 && string.Equals(trimmed[..equals].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(equals + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    // Reads the value of the first "# Key = value" comment under that key.
    private static string Comment(string? text, string key)
    {
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('#'))
            {
                continue;
            }

            var comment = trimmed[1..].TrimStart();
            var equals = comment.IndexOf('=');
            if (equals > 0 && string.Equals(comment[..equals].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                return comment[(equals + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    // Splits an Endpoint into its host, brackets aside, and its port.
    private static (string Host, int Port) Split(string endpoint)
    {
        var value = endpoint.Trim();
        var colon = value.LastIndexOf(':');
        var bracketed = value.StartsWith('[') && colon > value.LastIndexOf(']');
        if (colon <= 0 || (!bracketed && value.IndexOf(':') != colon))
        {
            return (value.Trim('[', ']'), 0);
        }

        var port = int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed is > 0 and <= 65535
                ? parsed
                : 0;

        return (value[..colon].Trim('[', ']'), port);
    }
}
