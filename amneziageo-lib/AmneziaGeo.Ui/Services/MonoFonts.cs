using Avalonia.Media;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Моноширинное семейство разметки и его замена системным.
/// </summary>
internal static class MonoFonts
{
    /// <summary>
    /// Шрифты моноширинного семейства разметки по порядку, до общего имени monospace.
    /// </summary>
    public static readonly string[] Design = ["IBM Plex Mono", "Cascadia Mono", "Consolas"];

    /// <summary>
    /// Сопоставление моноширинного семейства разметки с семейством <paramref name="system"/>.
    /// </summary>
    public static Dictionary<string, FontFamily> Mapped(string system)
    {
        return new Dictionary<string, FontFamily> { [Design[0]] = new FontFamily(system) };
    }
}
