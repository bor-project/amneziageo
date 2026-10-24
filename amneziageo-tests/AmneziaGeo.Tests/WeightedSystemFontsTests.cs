using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AmneziaGeo.Ui.Services;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class WeightedSystemFontsTests
{
    private const string DesignFamily = "IBM Plex Sans, Segoe UI, sans-serif";

    private const string MonoFamily = "IBM Plex Mono, Cascadia Mono, Consolas, monospace";

    [Fact]
    public void EveryWeight_GetsItsOwnFace()
    {
        using var scope = Platform();
        var fonts = new WeightedSystemFonts(new VariableFont());

        foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold, FontWeight.Normal })
        {
            Assert.True(fonts.TryGetGlyphTypeface("sans-serif", FontStyle.Normal, weight, FontStretch.Normal, out var face));
            Assert.Equal(weight, ((Face)face.PlatformTypeface).Asked);
        }
    }

    [Fact]
    public void AnUnknownFamily_IsNotFound()
    {
        using var scope = Platform();
        var fonts = new WeightedSystemFonts(new VariableFont());

        Assert.False(fonts.TryGetGlyphTypeface("IBM Plex Sans", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
    }

    [Fact]
    public void TheDesignFamily_TakesTheAskedWeight()
    {
        using var scope = Platform();
        AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(WeightedSystemFonts.Options("sans-serif", "monospace"));
        var fonts = new FontManager(new VariableFont());
        WeightedSystemFonts.Register(fonts);

        Assert.True(fonts.TryGetGlyphTypeface(new Typeface(DesignFamily, FontStyle.Normal, FontWeight.Normal), out var regular));
        Assert.True(fonts.TryGetGlyphTypeface(new Typeface(DesignFamily, FontStyle.Normal, FontWeight.SemiBold), out var semibold));
        Assert.True(fonts.TryGetGlyphTypeface(new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), out var bold));

        Assert.Equal(FontWeight.Normal, ((Face)regular.PlatformTypeface).Asked);
        Assert.Equal(FontWeight.SemiBold, ((Face)semibold.PlatformTypeface).Asked);
        Assert.Equal(FontWeight.Bold, ((Face)bold.PlatformTypeface).Asked);
    }

    [Fact]
    public void TheDesignMono_TakesTheSystemMonoAtTheAskedWeight()
    {
        using var scope = Platform();
        AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(WeightedSystemFonts.Options("sans-serif", "monospace"));
        var fonts = new FontManager(new VariableFont());
        WeightedSystemFonts.Register(fonts);

        Assert.True(fonts.TryGetGlyphTypeface(new Typeface(MonoFamily, FontStyle.Normal, FontWeight.SemiBold), out var mono));

        Assert.Equal("Droid Sans Mono", mono.PlatformTypeface.FamilyName);
        Assert.Equal(FontWeight.SemiBold, ((Face)mono.PlatformTypeface).Asked);
    }

    [Fact]
    public void TheMonoMapping_ReachesTheSystemMonoWithoutTheCollection()
    {
        using var scope = Platform();
        AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(new FontManagerOptions { FontFamilyMappings = MonoFonts.Mapped("monospace") });
        var fonts = new FontManager(new VariableFont());

        Assert.True(fonts.TryGetGlyphTypeface(new Typeface(MonoFamily), out var mono));

        Assert.Equal("Droid Sans Mono", mono.PlatformTypeface.FamilyName);
    }

    // Платформа шрифтов и загрузчик ресурсов, без которых Avalonia не заводит коллекцию шрифтов.
    private static IDisposable Platform()
    {
        var scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<IFontManagerImpl>().ToConstant(new VariableFont());
        AvaloniaLocator.CurrentMutable.Bind<IAssetLoader>().ToConstant(new StandardAssetLoader());
        return scope;
    }

    // Системные шрифты с осью веса: любое начертание называет себя обычным.
    private sealed class VariableFont : IFontManagerImpl
    {
        public string GetDefaultFontFamilyName() => "sans-serif";

        public string[] GetInstalledFontFamilyNames(bool checkForUpdates = false) => ["sans-serif", "monospace"];

        public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight, FontStretch fontStretch,
            string? familyName, CultureInfo? culture, [NotNullWhen(true)] out IPlatformTypeface? platformTypeface)
        {
            platformTypeface = null;
            return false;
        }

        public bool TryCreateGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out IPlatformTypeface? platformTypeface)
        {
            platformTypeface = familyName switch
            {
                "sans-serif" => new Face(weight),
                "monospace" => new Face(weight, "Droid Sans Mono"),
                _ => null,
            };
            return platformTypeface is not null;
        }

        public bool TryCreateGlyphTypeface(Stream stream, FontSimulations fontSimulations,
            [NotNullWhen(true)] out IPlatformTypeface? platformTypeface)
        {
            platformTypeface = null;
            return false;
        }

        public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
        {
            familyTypefaces = null;
            return false;
        }
    }

    // Начертание, которое помнит запрошенный у платформы вес; из таблиц только те, без которых Avalonia не строит шрифт.
    private sealed class Face(FontWeight asked, string family = "Roboto") : IPlatformTypeface
    {
        private static readonly byte[] Cmap =
        [
            0, 0, 0, 1, 0, 3, 0, 1, 0, 0, 0, 12,
            0, 4, 0, 24, 0, 0, 0, 2, 0, 2, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0xFF, 0xFF, 0, 1, 0, 0,
        ];

        private static readonly byte[] Maxp = [0, 0, 0x50, 0, 0, 1];

        public FontWeight Asked { get; } = asked;

        public string FamilyName => family;

        public FontWeight Weight => FontWeight.Normal;

        public FontStyle Style => FontStyle.Normal;

        public FontStretch Stretch => FontStretch.Normal;

        public FontSimulations FontSimulations => FontSimulations.None;

        public bool TryGetStream([NotNullWhen(true)] out Stream? stream)
        {
            stream = null;
            return false;
        }

        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = tag.ToString() switch
            {
                "cmap" => Cmap,
                "maxp" => Maxp,
                _ => ReadOnlyMemory<byte>.Empty,
            };
            return !table.IsEmpty;
        }

        public void Dispose()
        {
        }
    }
}
