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
