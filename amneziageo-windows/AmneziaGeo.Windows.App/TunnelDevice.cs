using System.Security.Cryptography;
using System.Text;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// The system name a tunnel takes: its adapter, its service and its pipes. The engine accepts only
/// [A-Za-z0-9_=+.-] up to 32 characters and refuses the DOS device names, so a configuration called anything
/// else - a copy named "fi (2)", a name in Cyrillic - is carried under a folded name instead of failing to
/// start with no error at all.
/// </summary>
internal static class TunnelDevice
{
    private const int MaxLength = 32;
    private const int FingerprintChars = 8;
    private const string Unnamed = "tunnel";

    private static readonly string[] _deviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Whether the engine takes the name as it stands.
    /// </summary>
    public static bool IsAcceptable(string name)
    {
        return name.Length is > 0 and <= MaxLength
            && name.All(Allowed)
            && !_deviceNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The adapter, service and pipe name of a configuration.
    /// </summary>
    public static string NameOf(string configName)
    {
        var name = configName ?? string.Empty;
        if (IsAcceptable(name))
        {
            return name;
        }

        var folded = Fold(name);
        var room = MaxLength - FingerprintChars - 1;
        if (folded.Length > room)
        {
            folded = folded[..room].Trim('-');
        }

        // The fingerprint keeps two names that fold onto the same text on adapters of their own.
        return $"{(folded.Length > 0 ? folded : Unnamed)}-{Fingerprint(name)}";
    }

    private static bool Allowed(char c)
    {
        return c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '=' or '+' or '.' or '-';
    }

    private static string Fold(string name)
    {
        var folded = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            var next = Allowed(c) ? c : '-';
            if (next != '-' || (folded.Length > 0 && folded[^1] != '-'))
            {
                folded.Append(next);
            }
        }

        return folded.ToString().Trim('-');
    }

    private static string Fingerprint(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return Convert.ToHexStringLower(hash.AsSpan(0, FingerprintChars / 2));
    }
}
