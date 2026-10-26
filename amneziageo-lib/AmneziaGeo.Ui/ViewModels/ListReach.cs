namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Что список маршрутизации отдаёт туннелю сверх названного в нём.
/// </summary>
internal enum ListReach
{
    /// <summary>
    /// Только названное.
    /// </summary>
    Named,

    /// <summary>
    /// Все приложения: список ведёт через VPN всё, кроме выбранного.
    /// </summary>
    AllApps,

    /// <summary>
    /// Весь UDP всех приложений.
    /// </summary>
    AllUdp,
}
