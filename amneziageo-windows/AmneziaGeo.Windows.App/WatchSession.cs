using System.Security.Cryptography;
using System.Text;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Names the monitoring sessions a tunnel reads connections and name lookups from.
/// </summary>
internal static class WatchSession
{
    /// <summary>
    /// The session of connections.
    /// </summary>
    public const string Flows = "AmneziaGeoFlow";

    /// <summary>
    /// The session of name lookups.
    /// </summary>
    public const string Names = "AmneziaGeoDns";

    /// <summary>
    /// The session of a kind for a tunnel: the kind alone for the tunnel that carries the machine, the kind with a
    /// mark of the device for any other tunnel of the set.
    /// </summary>
    public static string Name(string kind, bool carriesDefault, string device)
    {
        return carriesDefault ? kind : $"{kind}.{Mark(device)}";
    }

    // The letters and digits of the device with the start of its digest, so two devices never share a mark.
    private static string Mark(string device)
    {
        var kept = new string([.. device.Where(char.IsAsciiLetterOrDigit).Take(24)]);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(device)))[..8];
        return kept.Length > 0 ? $"{kept}-{digest}" : digest;
    }
}
