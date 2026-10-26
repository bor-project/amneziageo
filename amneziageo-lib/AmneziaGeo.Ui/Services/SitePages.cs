using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Страницы сайта, на которые ведут ссылки окна.
/// </summary>
internal static class SitePages
{
    private const string Site = "https://bor-project.github.io/amneziageo/";

    /// <summary>
    /// Окно, в котором стоит главный вид: через него страница уходит браузеру системы.
    /// </summary>
    public static TopLevel? Host { get; set; }

    /// <summary>
    /// Открывает раздел о том, какие приложения идут через VPN у списка «Всё, кроме выбранного» и со всем UDP.
    /// </summary>
    public static IAsyncRelayCommand FullModeCommand { get; } = new AsyncRelayCommand(() => OpenAsync(FullMode(Loc.Instance.Culture)));

    /// <summary>
    /// Тот же раздел на странице маршрутизации устройства и на языке окна.
    /// </summary>
    public static string FullMode(CultureInfo culture)
    {
        var device = OperatingSystem.IsAndroid() ? "android" : OperatingSystem.IsLinux() ? "linux" : "windows";
        var page = culture.TwoLetterISOLanguageName == "ru" ? "routing.ru.html" : "routing.html";
        return $"{Site}{device}/menu/{page}#full-mode";
    }

    // Отдаёт страницу браузеру системы.
    private static async Task OpenAsync(string url)
    {
        if (Host?.Launcher is not { } launcher)
        {
            return;
        }

        await launcher.LaunchUriAsync(new Uri(url)).ConfigureAwait(false);
    }
}
