using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// An app rule is read against a named application the way the matcher reads it: by package family, image,
/// folder or service, and never by one text holding another.
/// </summary>
public class AppRuleCoverTests
{
    private const string Folder = @"app:dir=%PROGRAMFILES%\WindowsApps\5319275A.WhatsAppDesktop_2.2628.101.0_x64__cv1g1gvanyjgm";

    [Fact]
    public void APackageFolderRuleCoversItsPackageWhateverTheVersion()
    {
        Assert.True(AppRuleCover.Covers(Folder, "app:pkg=5319275A.WhatsAppDesktop_cv1g1gvanyjgm"));
    }

    [Fact]
    public void APackageFolderRuleDoesNotCoverTheTwinPackage()
    {
        Assert.False(AppRuleCover.Covers(Folder, "app:pkg=5319275A.51895FA4EA97F_cv1g1gvanyjgm"));
    }

    [Fact]
    public void APackageRuleDoesNotCoverAFamilyItMerelyHolds()
    {
        Assert.False(AppRuleCover.Covers("app:pkg=5319275A.WhatsAppDesktop_cv1g1gvanyjgm", "app:pkg=WhatsAppDesktop_cv1g1gvanyjgm"));
    }

    [Fact]
    public void AFolderRuleCoversAnImageBelowIt()
    {
        Assert.True(AppRuleCover.Covers(@"app:dir=%LOCALAPPDATA%\Discord", @"app:path=%LOCALAPPDATA%\Discord\app-1.2.3\Discord.exe"));
    }

    [Fact]
    public void AFolderRuleDoesNotCoverANeighbourFolder()
    {
        Assert.False(AppRuleCover.Covers(@"app:dir=%LOCALAPPDATA%\Discord", @"app:path=%LOCALAPPDATA%\DiscordCanary\Discord.exe"));
    }

    [Fact]
    public void ANameRuleCoversTheImageOfThatName()
    {
        Assert.True(AppRuleCover.Covers("app:name=Discord.exe", @"app:path=%LOCALAPPDATA%\Discord\Discord.exe"));
    }

    [Fact]
    public void APathRuleCoversTheBareNameOfItsImage()
    {
        Assert.True(AppRuleCover.Covers(@"app:path=%LOCALAPPDATA%\Discord\Discord.exe", "app:Discord.exe"));
    }

    [Fact]
    public void AServiceRuleCoversOnlyThatService()
    {
        Assert.True(AppRuleCover.Covers("app:svc=Spooler", "app:svc=Spooler"));
        Assert.False(AppRuleCover.Covers("app:svc=Spool", "app:svc=Spooler"));
    }

    [Fact]
    public void ABarePackageFamilyIsReadAsOne()
    {
        Assert.True(AppRuleCover.Covers("app:pkg=5319275A.WhatsAppDesktop_cv1g1gvanyjgm", "app:5319275A.WhatsAppDesktop_cv1g1gvanyjgm"));
    }
}
