using System.Globalization;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The About block links the project site in the language of the window: the Russian home page for Russian, the
/// English one for any other language.
/// </summary>
public sealed class ProjectSiteTests
{
    [Theory]
    [InlineData("ru")]
    [InlineData("ru-RU")]
    public void ARussianWindow_LinksTheRussianHomePage(string culture)
    {
        var url = GeneralViewModel.SiteUrlFor(CultureInfo.GetCultureInfo(culture));

        Assert.Equal("https://bor-project.github.io/amneziageo/index.ru.html", url);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("de")]
    public void AnyOtherWindow_LinksTheEnglishHomePage(string culture)
    {
        var url = GeneralViewModel.SiteUrlFor(CultureInfo.GetCultureInfo(culture));

        Assert.Equal("https://bor-project.github.io/amneziageo/", url);
    }
}
