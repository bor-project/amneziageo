using System.Linq;

namespace AmneziaGeo.Ipc;

/// <summary>
/// In-band encoding for a localizable IPC ack message.
/// </summary>
public static class IpcMessage
{
    // Marker tags a payload as a localization key; separator delimits the key and its arguments.
    private const char Marker = '\u0001';
    private const char Separator = '\u001F';

    /// <summary>
    /// Encodes a resource key and its format arguments as an ack payload.
    /// </summary>
    public static string Key(string key, params object?[] args)
    {
        if (args is null || args.Length == 0)
        {
            return Marker + key;
        }

        return Marker + key + Separator + string.Join(Separator, args.Select(a => a?.ToString() ?? string.Empty));
    }

    /// <summary>
    /// Decodes a payload produced by Key. Returns false for any message without the marker.
    /// </summary>
    public static bool TryParse(string? message, out string key, out string[] args)
    {
        key = string.Empty;
        args = [];
        if (string.IsNullOrEmpty(message) || message[0] != Marker)
        {
            return false;
        }

        var parts = message[1..].Split(Separator);
        key = parts[0];
        args = parts.Length > 1 ? parts[1..] : [];
        return true;
    }
}
