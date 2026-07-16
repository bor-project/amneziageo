namespace AmneziaGeo.Windows.Tray;

/// <summary>
/// The context-menu labels, resolved once from the saved UI language (the tray-lang marker), falling back to
/// the OS UI language. A tiny table instead of the localization stack, so the native image carries no cultures.
/// </summary>
internal static class Labels
{
    /// <summary>
    /// «Открыть лаунчер» / "Open launcher": surfaces the quick-launch window.
    /// </summary>
    public static string Open { get; private set; } = "Open launcher";

    /// <summary>
    /// «Подключить» / "Connect": brings the tunnel up (enabled only with an active profile).
    /// </summary>
    public static string Connect { get; private set; } = "Connect";

    /// <summary>
    /// «Отключить» / "Disconnect": brings the tunnel down; the tray stays resident.
    /// </summary>
    public static string Disconnect { get; private set; } = "Disconnect";

    /// <summary>
    /// «Выход» / "Exit": unloads the tray while the tunnel is down.
    /// </summary>
    public static string Exit { get; private set; } = "Exit";

    /// <summary>
    /// «Выход (отключит VPN)» / "Exit (disconnects VPN)": Exit label while a tunnel is up, so quitting never
    /// silently drops protection.
    /// </summary>
    public static string ExitConnected { get; private set; } = "Exit (disconnects VPN)";

    /// <summary>
    /// «Подключение выполняется» / "Connection in progress": balloon body when the tunnel starts coming up.
    /// </summary>
    public static string ConnectingInfo { get; private set; } = "Connection in progress";

    /// <summary>
    /// «Подключение активно» / "Connection is active": balloon body on a fresh connect.
    /// </summary>
    public static string ConnectedInfo { get; private set; } = "Connection is active";

    /// <summary>
    /// «Не удалось подключиться» / "Connection failed": warning balloon body when a connect attempt drops back.
    /// </summary>
    public static string ConnectFailedInfo { get; private set; } = "Connection failed";

    /// <summary>
    /// «Соединение разорвано» / "Connection lost": warning balloon body when a live tunnel drops (#192).
    /// </summary>
    public static string DisconnectedInfo { get; private set; } = "Connection lost";

    /// <summary>
    /// Tooltip status «Подключено» / "Connected".
    /// </summary>
    public static string StatusConnected { get; private set; } = "Connected";

    /// <summary>
    /// Tooltip status «Отключено» / "Disconnected".
    /// </summary>
    public static string StatusDisconnected { get; private set; } = "Disconnected";

    /// <summary>
    /// Tooltip status «Подключение…» / "Connecting…".
    /// </summary>
    public static string StatusConnecting { get; private set; } = "Connecting…";

    /// <summary>
    /// Tooltip status «Отключение…» / "Disconnecting…".
    /// </summary>
    public static string StatusDisconnecting { get; private set; } = "Disconnecting…";

    /// <summary>
    /// Resolves the labels for the current language.
    /// </summary>
    public static void Load()
    {
        if (IsRussian(ReadSavedLanguage()))
        {
            Open = "Открыть лаунчер";
            Connect = "Подключить";
            Disconnect = "Отключить";
            Exit = "Выход";
            ExitConnected = "Выход (отключит VPN)";
            ConnectingInfo = "Подключение выполняется";
            ConnectedInfo = "Подключение активно";
            ConnectFailedInfo = "Не удалось подключиться";
            DisconnectedInfo = "Соединение разорвано";
            StatusConnected = "Подключено";
            StatusDisconnected = "Отключено";
            StatusConnecting = "Подключение…";
            StatusDisconnecting = "Отключение…";
        }
    }

    // The tray-lang marker holds the "ru"/"en" token (empty follows the system language), written by the app on
    // save; the tray reads it directly so it stays free of the SQLite state store the app now keeps prefs in.
    private static string ReadSavedLanguage()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmneziaGeo",
                "tray-lang");
            return File.ReadAllText(path).Trim();
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
