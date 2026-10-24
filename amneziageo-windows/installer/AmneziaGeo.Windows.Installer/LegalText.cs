using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// Kind of a block of the terms.
/// </summary>
public enum LegalBlockKind
{
    Heading,
    Paragraph,
    Bullet,
}

/// <summary>
/// A heading, a paragraph or an item of a list of the terms.
/// </summary>
public sealed record LegalBlock(LegalBlockKind Kind, string Text)
{
    /// <summary>
    /// Whether the block is a heading.
    /// </summary>
    public bool IsHeading => Kind == LegalBlockKind.Heading;

    /// <summary>
    /// Whether the block is an item of a list.
    /// </summary>
    public bool IsBullet => Kind == LegalBlockKind.Bullet;

    /// <inheritdoc/>
    public override string ToString() => Text;
}

/// <summary>
/// The terms of use or the privacy notes the installer carries, in the language of its window.
/// </summary>
public sealed partial class LegalText
{
    /// <summary>
    /// ctor
    /// </summary>
    private LegalText(int number, string footer, IReadOnlyList<LegalBlock> blocks)
    {
        Number = number;
        Footer = footer;
        Blocks = blocks;
    }

    /// <summary>
    /// The number of the version of the text, 0 when it names none.
    /// </summary>
    public int Number { get; }

    /// <summary>
    /// The version of the text and its license, under the text.
    /// </summary>
    public string Footer { get; }

    /// <summary>
    /// The text without its title and version line.
    /// </summary>
    public IReadOnlyList<LegalBlock> Blocks { get; }

    /// <summary>
    /// Reads TERMS or PRIVACY: Russian for a Russian window, English for any other.
    /// </summary>
    public static LegalText Load(string document, CultureInfo culture)
    {
        var name = culture.TwoLetterISOLanguageName == "ru" ? document + ".ru.md" : document + ".md";
        using var stream = typeof(LegalText).Assembly.GetManifestResourceStream("Legal." + name);
        if (stream is null)
        {
            return new LegalText(0, "GPL-3.0", []);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var blocks = Parse(reader.ReadToEnd());
        var version = blocks.Find(block => block.Kind == LegalBlockKind.Paragraph && VersionLine().IsMatch(block.Text));
        if (version is null)
        {
            return new LegalText(0, "GPL-3.0", blocks);
        }

        blocks.Remove(version);
        var number = int.Parse(VersionLine().Match(version.Text).Groups[2].Value, CultureInfo.InvariantCulture);
        return new LegalText(number, version.Text + " · GPL-3.0", blocks);
    }

    /// <summary>
    /// Splits the Markdown of a text into headings, paragraphs and items of lists, without its title.
    /// </summary>
    public static List<LegalBlock> Parse(string markdown)
    {
        var blocks = new List<LegalBlock>();
        var kind = LegalBlockKind.Paragraph;
        var text = new StringBuilder();

        void Flush()
        {
            if (text.Length > 0)
            {
                blocks.Add(new LegalBlock(kind, text.ToString()));
                text.Clear();
            }
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("# ", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush();
                kind = line[0] == '#' ? LegalBlockKind.Heading : LegalBlockKind.Bullet;
                text.Append(line[(line.IndexOf(' ') + 1)..]);
                if (kind == LegalBlockKind.Heading)
                {
                    Flush();
                }

                continue;
            }

            if (text.Length == 0)
            {
                kind = LegalBlockKind.Paragraph;
            }
            else
            {
                text.Append(' ');
            }

            text.Append(line);
        }

        Flush();
        return blocks;
    }

    [GeneratedRegex(@"^(Версия|Version) (\d+)")]
    private static partial Regex VersionLine();
}
