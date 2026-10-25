using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AmneziaGeo.Ui.Services;

/// <summary>
/// Условия использования, конфиденциальность и сторонние компоненты, вшитые в сборку из корня репозитория.
/// </summary>
internal static partial class LegalTexts
{
    /// <summary>
    /// Версия условий и конфиденциальности, которую окно просит принять.
    /// </summary>
    public const int Version = 2;

    /// <summary>
    /// Условия использования.
    /// </summary>
    public const string Terms = "TERMS";

    /// <summary>
    /// Конфиденциальность.
    /// </summary>
    public const string Privacy = "PRIVACY";

    /// <summary>
    /// Сторонние компоненты.
    /// </summary>
    public const string ThirdParty = "THIRD-PARTY";

    /// <summary>
    /// Все тексты.
    /// </summary>
    public static IReadOnlyList<string> Documents { get; } = [Terms, Privacy, ThirdParty];

    /// <summary>
    /// Приняты ли условия текущей версии.
    /// </summary>
    public static bool Accepted(UiPreferences prefs) => prefs.AcceptedTerms >= Version;

    /// <summary>
    /// Записывает принятие текущей версии.
    /// </summary>
    public static void Accept(UiPreferences prefs, DateTime now)
    {
        prefs.AcceptedTerms = Version;
        prefs.AcceptedTermsOn = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        prefs.Save();
    }

    /// <summary>
    /// Текст на языке окна: русский для русского, английский для остальных.
    /// </summary>
    public static string Read(string document, CultureInfo culture)
    {
        var name = culture.TwoLetterISOLanguageName == "ru" ? document + ".ru.md" : document + ".md";
        using var stream = typeof(LegalTexts).Assembly.GetManifestResourceStream("Legal." + name);
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Текст, разобранный на заголовок, строку версии и остальное.
    /// </summary>
    public static LegalDocument Load(string document, CultureInfo culture)
    {
        var blocks = Parse(Read(document, culture));
        var title = blocks.FirstOrDefault(block => block.Kind == LegalBlockKind.Title)?.Text ?? string.Empty;
        var version = blocks.FirstOrDefault(block => block.Kind == LegalBlockKind.Paragraph && VersionLine().IsMatch(block.Text));
        var body = blocks.Where(block => block.Kind != LegalBlockKind.Title && !ReferenceEquals(block, version)).ToList();
        return new LegalDocument(title, version?.Text ?? string.Empty, body);
    }

    /// <summary>
    /// Разбирает Markdown текста на заголовки, абзацы и пункты списка.
    /// </summary>
    public static IReadOnlyList<LegalBlock> Parse(string markdown)
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
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (MarkOf(line) is { } mark)
            {
                Flush();
                kind = mark;
                text.Append(line[(line.IndexOf(' ') + 1)..]);
                if (kind != LegalBlockKind.Bullet)
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

    // Строка заголовка или пункта списка.
    private static LegalBlockKind? MarkOf(string line)
    {
        if (line.StartsWith("# ", StringComparison.Ordinal))
        {
            return LegalBlockKind.Title;
        }

        if (line.StartsWith("## ", StringComparison.Ordinal))
        {
            return LegalBlockKind.Heading;
        }

        return line.StartsWith("- ", StringComparison.Ordinal) ? LegalBlockKind.Bullet : null;
    }

    [GeneratedRegex(@"^(Версия|Version) \d+")]
    private static partial Regex VersionLine();
}

/// <summary>
/// Разобранный текст: заголовок, строка версии (пусто у текста без версии) и остальное.
/// </summary>
internal sealed record LegalDocument(string Title, string Version, IReadOnlyList<LegalBlock> Body);

/// <summary>
/// Кусок текста.
/// </summary>
internal sealed record LegalBlock(LegalBlockKind Kind, string Text)
{
    /// <summary>
    /// Заголовок раздела.
    /// </summary>
    public bool IsHeading => Kind == LegalBlockKind.Heading;

    /// <summary>
    /// Абзац.
    /// </summary>
    public bool IsParagraph => Kind == LegalBlockKind.Paragraph;

    /// <summary>
    /// Пункт списка.
    /// </summary>
    public bool IsBullet => Kind == LegalBlockKind.Bullet;
}

/// <summary>
/// Вид куска текста.
/// </summary>
internal enum LegalBlockKind
{
    /// <summary>
    /// Заголовок текста.
    /// </summary>
    Title,

    /// <summary>
    /// Заголовок раздела.
    /// </summary>
    Heading,

    /// <summary>
    /// Абзац.
    /// </summary>
    Paragraph,

    /// <summary>
    /// Пункт списка.
    /// </summary>
    Bullet,
}
