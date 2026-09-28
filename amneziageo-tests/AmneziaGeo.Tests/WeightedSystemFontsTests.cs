using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AmneziaGeo.Ui.Services;
using Avalonia;
using Avalonia.Media;
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
        var fonts = new WeightedSystemFonts();
        fonts.Initialize(new VariableFont());

        foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold, FontWeight.Normal })
        {
            Assert.True(fonts.TryGetGlyphTypeface("sans-serif", FontStyle.Normal, weight, FontStretch.Normal, out var face));
            Assert.Equal(weight, ((Face)face).Asked);
        }
    }

    [Fact]
    public void AnUnknownFamily_IsNotFound()
    {
        var fonts = new WeightedSystemFonts();
        fonts.Initialize(new VariableFont());

        Assert.False(fonts.TryGetGlyphTypeface("IBM Plex Sans", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
    }

    [Fact]
    public void TheDesignFamily_TakesTheAskedWeight()
    {
        using var scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(WeightedSystemFonts.Options("sans-serif", "monospace"));
        var fonts = new FontManager(new VariableFont());
        WeightedSystemFonts.Register(fonts);

        Assert.True(fonts.TryGetGlyphTypeface(new Typeface(DesignFamily, FontStyle.Normal, FontWeight.Normal), out var regular));
        Assert.True(fonts.TryGetGlyphTypeface(new Typeface(DesignFamily, FontStyle.Normal, FontWeight.SemiBold), out var semibold));
        Assert.True(fonts.TryGetGlyphTypeface(new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), out var bold));

        Assert.Equal(FontWeight.Normal, ((Face)regular).Asked);
        Assert.Equal(FontWeight.SemiBold, ((Face)semibold).Asked);
        Assert.Equal(FontWeight.Bold, ((Face)bold).Asked);
    }

    [Fact]
    public void TheDesignMono_TakesTheSystemMonoAtTheAskedWeight()
    {
        using var scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(WeightedSystemFonts.Options("sans-serif", "monospace"));
        var fonts = new FontManager(new VariableFont());
        WeightedSystemFonts.Register(fonts);

        Assert.True(fonts.TryGetGlyphTypeface(new Typeface(MonoFamily, FontStyle.Normal, FontWeight.SemiBold), out var mono));

        Assert.Equal("Droid Sans Mono", mono.FamilyName);
        Assert.Equal(FontWeight.SemiBold, ((Face)mono).Asked);
    }

    [Fact]
    public void TheMonoMapping_ReachesTheSystemMonoWithoutTheCollection()
    {
        using var scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(new FontManagerOptions { FontFamilyMappings = MonoFonts.Mapped("monospace") });
        var fonts = new FontManager(new VariableFont());

        Assert.True(fonts.TryGetGlyphTypeface(new Typeface(MonoFamily), out var mono));

        Assert.Equal("Droid Sans Mono", mono.FamilyName);
    }

    // Системные шрифты с осью веса: любое начертание называет себя обычным.
    private sealed class VariableFont : IFontManagerImpl
    {
        public string GetDefaultFontFamilyName() => "sans-serif";

        public string[] GetInstalledFontFamilyNames(bool checkForUpdates = false) => ["sans-serif", "monospace"];

        public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight, FontStretch fontStretch,
            CultureInfo? culture, out Typeface typeface)
        {
            typeface = default;
            return false;
        }

        public bool TryCreateGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
        {
            glyphTypeface = familyName switch
            {
                "sans-serif" => new Face(weight),
                "monospace" => new Face(weight, "Droid Sans Mono"),
                _ => null,
            };
            return glyphTypeface is not null;
        }

        public bool TryCreateGlyphTypeface(Stream stream, FontSimulations fontSimulations,
            [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
        {
            glyphTypeface = null;
            return false;
        }
    }

    // Начертание, которое помнит запрошенный у платформы вес.
    private sealed class Face(FontWeight asked, string family = "Roboto") : IGlyphTypeface
    {
        public FontWeight Asked { get; } = asked;

        public string FamilyName => family;

        public FontWeight Weight => FontWeight.Normal;

        public FontStyle Style => FontStyle.Normal;

        public FontStretch Stretch => FontStretch.Normal;

        public int GlyphCount => 0;

        public FontMetrics Metrics => default;

        public FontSimulations FontSimulations => FontSimulations.None;

        public ushort GetGlyph(uint codepoint) => 0;

        public bool TryGetGlyph(uint codepoint, out ushort glyph)
        {
            glyph = 0;
            return false;
        }

        public int GetGlyphAdvance(ushort glyph) => 0;

        public int[] GetGlyphAdvances(ReadOnlySpan<ushort> glyphs) => new int[glyphs.Length];

        public ushort[] GetGlyphs(ReadOnlySpan<uint> codepoints) => new ushort[codepoints.Length];

        public bool TryGetGlyphMetrics(ushort glyph, out GlyphMetrics metrics)
        {
            metrics = default;
            return false;
        }

        public bool TryGetTable(uint tag, out byte[] table)
        {
            table = [];
            return false;
        }

        public void Dispose()
        {
        }
    }
}
