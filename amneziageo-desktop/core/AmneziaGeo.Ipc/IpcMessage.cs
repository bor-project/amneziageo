using System.Linq;

namespace AmneziaGeo.Ipc;

/// <summary>
/// In-band encoding for a localizable IPC ack message (#106). The agent process does not localize; it puts a
/// resource KEY plus its format arguments into <see cref="IpcAck.Message"/> via <see cref="Key"/>, and the UI
/// translates it centrally (in AgentConnection, the one place every command reply flows through) with
/// <see cref="TryParse"/> before display. A message without the marker - raw text, a config payload, a file
/// path, an exception message - is passed through untranslated, so only intentionally-keyed replies localize.
/// </summary>
public static class IpcMessage
{
    // A control char no genuine message starts with, tagging a payload as "a localization key", and a unit
    // separator between the key and its stringified arguments.
    private const char Marker = '\u0001';
    private const char Separator = '\u001F';

    /// <summary>Encodes a resource key and its (stringified) format arguments as an ack payload.</summary>
    public static string Key(string key, params object?[] args)
    {
        if (args is null || args.Length == 0)
        {
            return Marker + key;
        }

        return Marker + key + Separator + string.Join(Separator, args.Select(a => a?.ToString() ?? string.Empty));
    }

    /// <summary>
    /// Decodes a payload produced by <see cref="Key"/>. Returns false - leaving the message to be shown as-is -
    /// for any message that does not carry the marker.
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
