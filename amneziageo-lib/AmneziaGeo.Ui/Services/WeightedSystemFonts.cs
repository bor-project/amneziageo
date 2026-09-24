using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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

    private FontFamily[]? _families;
    private IFontManagerImpl? _platform;

    /// <inheritdoc/>
    public override Uri Key => CollectionKey;

    /// <inheritdoc/>
    public override int Count => Families.Length;

    /// <inheritdoc/>
    public override FontFamily this[int index] => Families[index];

    private FontFamily[] Families =>
        _families ??= (_platform?.GetInstalledFontFamilyNames(false) ?? []).Select(name => new FontFamily(FamilyOf(name))).ToArray();

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
        fonts.AddFontCollection(new WeightedSystemFonts());
    }

    /// <summary>
    /// Имя системного семейства в этой коллекции.
    /// </summary>
    public static string FamilyOf(string name) => $"{CollectionKey.AbsoluteUri}#{name}";

    /// <inheritdoc/>
    public override IEnumerator<FontFamily> GetEnumerator() => ((IEnumerable<FontFamily>)Families).GetEnumerator();

    /// <inheritdoc/>
    public override void Initialize(IFontManagerImpl fontManager)
    {
        _platform = fontManager;
    }

    /// <inheritdoc/>
    public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
    {
        var faces = _glyphTypefaceCache.GetOrAdd(familyName, static _ => new ConcurrentDictionary<FontCollectionKey, IGlyphTypeface?>());
        var key = new FontCollectionKey(style, weight, stretch);
        if (!faces.TryGetValue(key, out glyphTypeface))
        {
            glyphTypeface = faces.GetOrAdd(key, Create(familyName, style, weight, stretch));
        }

        return glyphTypeface is not null;
    }

    // Берёт у платформы начертание под запрошенные стиль, вес и ширину.
    private IGlyphTypeface? Create(string familyName, FontStyle style, FontWeight weight, FontStretch stretch)
    {
        if (_platform is not null && _platform.TryCreateGlyphTypeface(familyName, style, weight, stretch, out var created))
        {
            return created;
        }

        return null;
    }
}
