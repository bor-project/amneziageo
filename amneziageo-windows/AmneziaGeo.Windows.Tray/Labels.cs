namespace AmneziaGeo.Windows.Tray;

/// <summary>
/// The context-menu labels, resolved once from the saved UI language (the tray-lang marker), falling back to
/// the OS UI language. A tiny table instead of the localization stack, so the native image carries no cultures.
/// </summary>
internal static class Labels
{
    /// <summary>
    /// «Открыть» / "Open": surfaces the main window.
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
    /// «Выход» / "Exit": unloads the tray while the tunnel is down.
    /// </summary>
    public static string Exit { get; private set; } = "Exit";

    /// <summary>
    /// «Выход (отключит VPN)» / "Exit (disconnects VPN)": Exit label while a tunnel is up, so quitting never
    /// silently drops protection.
    /// </summary>
    public static string ExitConnected { get; private set; } = "Exit (disconnects VPN)";

    /// <summary>
    /// «Подключение…» / "Connecting…": balloon body when the tunnel starts coming up (#9).
    /// </summary>
    public static string ConnectingInfo { get; private set; } = "Connecting…";

    /// <summary>
    /// «Подключение установлено» / "Connection established": balloon body on a fresh connect (#10).
    /// </summary>
    public static string ConnectedInfo { get; private set; } = "Connection established";

    /// <summary>
    /// «Не удалось установить подключение» / "Could not establish the connection": warning balloon body when a
    /// connect attempt drops back (#11).
    /// </summary>
    public static string ConnectFailedInfo { get; private set; } = "Could not establish the connection";

    /// <summary>
    /// «Соединение разорвано» / "Connection lost": warning balloon body when a live tunnel drops (#192).
    /// </summary>
    public static string ConnectionLostInfo { get; private set; } = "Connection lost";

    /// <summary>
    /// «Отключение…» / "Disconnecting…": balloon body when the tunnel starts coming down (#12).
    /// </summary>
    public static string DisconnectingInfo { get; private set; } = "Disconnecting…";

    /// <summary>
    /// «Отключено» / "Disconnected": balloon body on a clean, user-initiated disconnect (#13).
    /// </summary>
    public static string DisconnectedInfo { get; private set; } = "Disconnected";

    /// <summary>
    /// «Не удалось завершить отключение» / "Could not finish the disconnect": warning balloon body when a
    /// teardown stalls with the tunnel still up (#14).
    /// </summary>
    public static string DisconnectFailedInfo { get; private set; } = "Could not finish the disconnect";

    /// <summary>
    /// «Проверить обновление» / "Check for updates": menu item that asks the agent to check now.
    /// </summary>
    public static string CheckUpdate { get; private set; } = "Check for updates";

    /// <summary>
    /// «Проверка обновления…» / "Checking for updates…": inactive menu item while a check runs (#15).
    /// </summary>
    public static string CheckingUpdate { get; private set; } = "Checking for updates…";

    /// <summary>
    /// «У вас последняя версия» / "You have the latest version": balloon body when a manual check finds no
    /// update (#15).
    /// </summary>
    public static string UpToDateInfo { get; private set; } = "You have the latest version";

    /// <summary>
    /// «Скачать обновление» / "Download update": menu item shown when an update is available to download.
    /// </summary>
    public static string DownloadUpdate { get; private set; } = "Download update";

    /// <summary>
    /// «Установить обновление» / "Install update": menu item shown once the setup is downloaded.
    /// </summary>
    public static string InstallUpdate { get; private set; } = "Install update";

    /// <summary>
    /// «Загрузка: {0}%» / "Downloading: {0}%": inactive menu item showing download progress (#17).
    /// </summary>
    public static string DownloadingUpdate { get; private set; } = "Downloading: {0}%";

    /// <summary>
    /// «Отменить загрузку» / "Cancel download": menu item that aborts a running download (#17).
    /// </summary>
    public static string CancelDownload { get; private set; } = "Cancel download";

    /// <summary>
    /// Balloon body when an update is found; a click starts the download.
    /// </summary>
    public static string UpdateFoundInfo { get; private set; } = "New version {0} available. Click to download.";

    /// <summary>
    /// Balloon body once the setup is downloaded; a click starts the install.
    /// </summary>
    public static string UpdateDownloadedInfo { get; private set; } = "Update {0} downloaded. Click to install.";

    /// <summary>
    /// «Обновление установлено» / "Update installed": balloon body after an update is applied.
    /// </summary>
    public static string UpdateInstalledInfo { get; private set; } = "Update installed";

    /// <summary>
    /// «Не удалось скачать обновление» / "Could not download the update": warning balloon body when a download
    /// fails (#8).
    /// </summary>
    public static string UpdateDownloadFailedInfo { get; private set; } = "Could not download the update";

    /// <summary>
    /// «Идёт загрузка обновления. Выйти и отменить её?» / "An update is downloading. Exit and cancel it?": exit
    /// confirmation while a download runs (#21).
    /// </summary>
    public static string ExitDownloadPrompt { get; private set; } = "An update is downloading. Exit and cancel it?";

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
            Open = "Открыть";
            Connect = "Подключить";
            Disconnect = "Отключить";
            Exit = "Выход";
            ExitConnected = "Выход (отключит VPN)";
            ConnectingInfo = "Подключение…";
            ConnectedInfo = "Подключение установлено";
            ConnectFailedInfo = "Не удалось установить подключение";
            ConnectionLostInfo = "Соединение разорвано";
            DisconnectingInfo = "Отключение…";
            DisconnectedInfo = "Отключено";
            DisconnectFailedInfo = "Не удалось завершить отключение";
            CheckUpdate = "Проверить обновление";
            CheckingUpdate = "Проверка обновления…";
            UpToDateInfo = "У вас последняя версия";
            DownloadUpdate = "Скачать обновление";
            InstallUpdate = "Установить обновление";
            DownloadingUpdate = "Загрузка: {0}%";
            CancelDownload = "Отменить загрузку";
            UpdateFoundInfo = "Доступна новая версия {0}. Нажмите, чтобы скачать.";
            UpdateDownloadedInfo = "Обновление {0} скачано. Нажмите, чтобы установить.";
            UpdateInstalledInfo = "Обновление установлено";
            UpdateDownloadFailedInfo = "Не удалось скачать обновление";
            ExitDownloadPrompt = "Идёт загрузка обновления. Выйти и отменить её?";
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
