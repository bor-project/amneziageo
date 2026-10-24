using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Системные шрифты, где начертание выбирается по запрошенному весу, а не по весу, который называет файл шрифта.
/// </summary>
internal sealed class WeightedSystemFonts : FontCollectionBase
{
    private static readonly Uri CollectionKey = new("fonts:weighted");

    private readonly IFontManagerImpl _platform;
    private readonly ConcurrentDictionary<(string Family, FontCollectionKey Key), GlyphTypeface?> _faces = new();

    /// <summary>
    /// ctor
    /// </summary>
    public WeightedSystemFonts(IFontManagerImpl platform)
    {
        _platform = platform;
        foreach (var name in platform.GetInstalledFontFamilyNames().Where(name => !string.IsNullOrEmpty(name)))
        {
            AddFontFamily(new FontFamily(FamilyOf(name)));
        }
    }

    /// <inheritdoc/>
    public override Uri Key => CollectionKey;

    /// <summary>
    /// Настройки шрифтов, в которых текст без своего семейства и моноширинный текст идут через эту коллекцию.
    /// </summary>
    public static FontManagerOptions Options(string defaultFamily, string monoFamily)
    {
        return new FontManagerOptions
        {
            DefaultFamilyName = FamilyOf(defaultFamily),
            FontFamilyMappings = MonoFonts.Mapped(FamilyOf(monoFamily)),
        };
    }

    /// <summary>
    /// Добавляет коллекцию в менеджер шрифтов.
    /// </summary>
    public static void Register(FontManager fonts)
    {
        if (AvaloniaLocator.Current.GetService(typeof(IFontManagerImpl)) is IFontManagerImpl platform)
        {
            fonts.AddFontCollection(new WeightedSystemFonts(platform));
        }
    }

    /// <summary>
    /// Имя системного семейства в этой коллекции.
    /// </summary>
    public static string FamilyOf(string name) => $"{CollectionKey.AbsoluteUri}#{name}";

    /// <inheritdoc/>
    public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        glyphTypeface = _faces.GetOrAdd((familyName, new FontCollectionKey(style, weight, stretch)), _ => Create(familyName, style, weight, stretch));
        return glyphTypeface is not null;
    }

    // Берёт у платформы начертание под запрошенные стиль, вес и ширину.
    private GlyphTypeface? Create(string familyName, FontStyle style, FontWeight weight, FontStretch stretch)
    {
        if (!_platform.TryCreateGlyphTypeface(familyName, style, weight, stretch, out var created))
        {
            return null;
        }

        try
        {
            return new GlyphTypeface(created);
        }
        catch (InvalidOperationException)
        {
            created.Dispose();
            return null;
        }
    }
}
