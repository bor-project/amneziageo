using System.Globalization;

using AmneziaGeo.Ui.Services;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Тексты условий, конфиденциальности и сторонних компонентов вшиты в окно на обоих языках и разбираются на куски.
/// </summary>
public sealed class LegalTextsTests
{
    public static TheoryData<string, string> Texts()
    {
        var data = new TheoryData<string, string>();
        foreach (var document in LegalTexts.Documents)
        {
            data.Add(document, "en-US");
            data.Add(document, "ru-RU");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void EveryText_IsThereWithATitleAndABody(string document, string culture)
    {
        var text = LegalTexts.Load(document, CultureInfo.GetCultureInfo(culture));

        Assert.NotEmpty(text.Title);
        Assert.Contains(text.Body, block => block.IsHeading);
        Assert.All(text.Body, block => Assert.NotEmpty(block.Text));
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void NoText_CarriesATable(string document, string culture)
    {
        var markdown = LegalTexts.Read(document, CultureInfo.GetCultureInfo(culture));

        Assert.DoesNotContain(markdown.Split('\n'), line => line.TrimStart().StartsWith('|'));
    }

    [Theory]
    [InlineData(LegalTexts.Terms, "en-US", "Version ")]
    [InlineData(LegalTexts.Terms, "ru-RU", "Версия ")]
    [InlineData(LegalTexts.Privacy, "en-US", "Version ")]
    [InlineData(LegalTexts.Privacy, "ru-RU", "Версия ")]
    public void TheTermsAndThePrivacy_NameTheVersionTheWindowAsksFor(string document, string culture, string word)
    {
        var text = LegalTexts.Load(document, CultureInfo.GetCultureInfo(culture));

        Assert.StartsWith(word + LegalTexts.Version.ToString(CultureInfo.InvariantCulture), text.Version);
        Assert.DoesNotContain(text.Body, block => block.Text == text.Version);
    }

    [Fact]
    public void ALanguageWithoutItsOwnText_TakesTheEnglishOne()
    {
        var german = LegalTexts.Read(LegalTexts.Terms, CultureInfo.GetCultureInfo("de-DE"));

        Assert.Equal(LegalTexts.Read(LegalTexts.Terms, CultureInfo.GetCultureInfo("en-US")), german);
    }

    [Fact]
    public void Markdown_SplitsIntoTitleHeadingsParagraphsAndBullets()
    {
        var blocks = LegalTexts.Parse("# Title\n\nVersion 1, today\n\n## Part\n\nOne line\nand the next.\n\n- first\n  goes on\n- second\n");

        Assert.Equal(
            [
                new LegalBlock(LegalBlockKind.Title, "Title"),
                new LegalBlock(LegalBlockKind.Paragraph, "Version 1, today"),
                new LegalBlock(LegalBlockKind.Heading, "Part"),
                new LegalBlock(LegalBlockKind.Paragraph, "One line and the next."),
                new LegalBlock(LegalBlockKind.Bullet, "first goes on"),
                new LegalBlock(LegalBlockKind.Bullet, "second"),
            ],
            blocks);
    }

    [Fact]
    public void TermsOfAnOlderVersion_AreNotAccepted()
    {
        Assert.False(LegalTexts.Accepted(new UiPreferences()));
        Assert.False(LegalTexts.Accepted(new UiPreferences { AcceptedTerms = LegalTexts.Version - 1 }));
        Assert.True(LegalTexts.Accepted(new UiPreferences { AcceptedTerms = LegalTexts.Version }));
    }
}
