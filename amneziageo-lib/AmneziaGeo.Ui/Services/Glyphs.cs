using Avalonia.Media;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Значки строк в наборах способов: набор строится в коде, где ресурсов разметки нет.
/// </summary>
internal static class Glyphs
{
    /// <summary>
    /// Файл.
    /// </summary>
    public static Geometry File { get; } = Geometry.Parse(
        "M13,9H18.5L13,3.5V9M6,2H14L20,8V20A2,2 0 0,1 18,22H6C4.89,22 4,21.1 4,20V4C4,2.89 4.89,2 6,2M15,18V16H6V18H15M18,14V12H6V14H18Z");

    /// <summary>
    /// Буфер обмена.
    /// </summary>
    public static Geometry Paste { get; } = Geometry.Parse(
        "M19,3H14.82C14.4,1.84 13.3,1 12,1C10.7,1 9.6,1.84 9.18,3H5A2,2 0 0,0 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5A2,2 0 0,0 19,3M12,3A1,1 0 0,1 13,4A1,1 0 0,1 12,5A1,1 0 0,1 11,4A1,1 0 0,1 12,3M7,7H17V5H19V19H5V5H7V7Z");

    /// <summary>
    /// QR-код.
    /// </summary>
    public static Geometry Qr { get; } = Geometry.Parse(
        "M3,11H5V13H3V11M11,5H13V9H11V5M9,11H13V15H11V13H9V11M15,11H17V13H19V11H21V13H19V15H21V19H19V21H17V19H13V21H11V17H15V15H17V13H15V11M19,19V15H17V19H19M15,3H21V9H15V3M17,5V7H19V5H17M3,3H9V9H3V3M5,5V7H7V5H5M3,15H9V21H3V15M5,17V19H7V17H5Z");

    /// <summary>
    /// Карандаш.
    /// </summary>
    public static Geometry Pencil { get; } = Geometry.Parse(
        "M20.71,7.04C21.1,6.65 21.1,6 20.71,5.63L18.37,3.29C18,2.9 17.35,2.9 16.96,3.29L15.12,5.12L18.87,8.87M3,17.25V21H6.75L17.81,9.93L14.06,6.18L3,17.25Z");

    /// <summary>
    /// Ссылка.
    /// </summary>
    public static Geometry Link { get; } = Geometry.Parse(
        "M3.9,12C3.9,10.29 5.29,8.9 7,8.9H11V7H7A5,5 0 0,0 2,12A5,5 0 0,0 7,17H11V15.1H7C5.29,15.1 3.9,13.71 3.9,12M8,13H16V11H8V13M17,7H13V8.9H17C18.71,8.9 20.1,10.29 20.1,12C20.1,13.71 18.71,15.1 17,15.1H13V17H17A5,5 0 0,0 22,12A5,5 0 0,0 17,7Z");

    /// <summary>
    /// Сохранение в файл.
    /// </summary>
    public static Geometry Download { get; } = Geometry.Parse(
        "M5,20H19V18H5M19,9H15V3H9V9H5L12,16L19,9Z");

    /// <summary>
    /// Копирование.
    /// </summary>
    public static Geometry Copy { get; } = Geometry.Parse(
        "M19,21H8V7H19M19,5H8A2,2 0 0,0 6,7V21A2,2 0 0,0 8,23H19A2,2 0 0,0 21,21V7A2,2 0 0,0 19,5M16,1H4A2,2 0 0,0 2,3V17H4V3H16V1Z");

    /// <summary>
    /// Передача в другое приложение.
    /// </summary>
    public static Geometry Share { get; } = Geometry.Parse(
        "M18,16.08C17.24,16.08 16.56,16.38 16.04,16.85L8.91,12.7C8.96,12.47 9,12.24 9,12C9,11.76 8.96,11.53 8.91,11.3L15.96,7.19C16.5,7.69 17.21,8 18,8A3,3 0 0,0 21,5A3,3 0 0,0 18,2A3,3 0 0,0 15,5C15,5.24 15.04,5.47 15.09,5.7L8.04,9.81C7.5,9.31 6.79,9 6,9A3,3 0 0,0 3,12A3,3 0 0,0 6,15C6.79,15 7.5,14.69 8.04,14.19L15.16,18.34C15.11,18.55 15.08,18.77 15.08,19C15.08,20.61 16.39,21.91 18,21.91C19.61,21.91 20.92,20.61 20.92,19A2.92,2.92 0 0,0 18,16.08Z");

    /// <summary>
    /// Установка обновления.
    /// </summary>
    public static Geometry Install { get; } = Geometry.Parse(
        "M5.12,5H18.87L17.93,3H5.93L5.12,5M20.54,5.23C20.83,5.57 21,6 21,6.5V19A2,2 0 0,1 19,21H5A2,2 0 0,1 3,19V6.5C3,6 3.17,5.57 3.46,5.23L4.84,3.55C5.11,3.22 5.53,3 6,3H18C18.47,3 18.89,3.22 19.15,3.55L20.54,5.23M12,18L16.5,13.5H13.75V10H10.25V13.5H7.5L12,18Z");

    /// <summary>
    /// Отказ.
    /// </summary>
    public static Geometry Close { get; } = Geometry.Parse(
        "M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z");
}
