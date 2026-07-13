using System.Text.Json;

namespace AmneziaGeo.Windows.Tray;

/// <summary>
/// The two context-menu labels, resolved once from the saved UI language (ui-prefs.json), falling back to the
/// OS UI language. A tiny table instead of the localization stack, so the native image carries no cultures.
/// </summary>
internal static class Labels
{
    /// <summary>
    /// «Открыть» / "Open": surfaces the GUI.
    /// </summary>
    public static string Open { get; private set; } = "Open";

    /// <summary>
    /// «Подключить» / "Connect": brings the tunnel up (enabled only with an active profile).
    /// </summary>
    public static string Connect { get; private set; } = "Connect";

    /// <summary>
    /// «Отключить» / "Disconnect": brings the tunnel down; the tray stays resident.
    /// </summary>
    public static string Disconnect { get; private set; } = "Disconnect";

    /// <summary>
    /// «Выход» / "Exit": disconnects if needed and unloads the tray.
    /// </summary>
    public static string Exit { get; private set; } = "Exit";

    /// <summary>
    /// «Подключение выполняется» / "Connection in progress": balloon body when the tunnel starts coming up.
    /// </summary>
    public static string ConnectingInfo { get; private set; } = "Connection in progress";

    /// <summary>
    /// «Подключение активно» / "Connection is active": balloon body on a fresh connect.
    /// </summary>
    public static string ConnectedInfo { get; private set; } = "Connection is active";

    /// <summary>
    /// Tooltip status «Подключено» / "Connected".
    /// </summary>
    public static string StatusConnected { get; private set; } = "Connected";

    /// <summary>
    /// Tooltip status «Отключено» / "Disconnected".
    /// </summary>
    public static string StatusDisconnected { get; private set; } = "Disconnected";

    /// <summary>
    /// Resolves the labels for the current language.
    /// </summary>
    public static void Load()
    {
        if (IsRussian(ReadSavedLanguage()))
        {
            Open = "Открыть";
            Connect = "Подключить";
            Disconnect = "Отключить";
            Exit = "Выход";
            ConnectingInfo = "Подключение выполняется";
            ConnectedInfo = "Подключение активно";
            StatusConnected = "Подключено";
            StatusDisconnected = "Отключено";
        }
    }

    // ui-prefs.json "Language": "ru"/"en" token, empty follows the system language.
    private static string ReadSavedLanguage()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmneziaGeo",
                "ui-prefs.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("Language", out var lang) ? lang.GetString() ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsRussian(string language)
    {
        if (language.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Empty pref: follow the OS UI language (primary language id 0x19 == Russian).
        return (Native.GetUserDefaultUILanguage() & 0x3FF) == 0x19;
    }
}
