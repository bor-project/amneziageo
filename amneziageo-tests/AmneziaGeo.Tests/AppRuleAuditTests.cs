using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// An app rule is checked against what is installed: a rule naming nothing is reported, and so is a package whose
/// program another installed package runs while no rule names that one.
/// </summary>
public class AppRuleAuditTests
{
    private static readonly string Whats = "5319275A.WhatsAppDesktop_cv1g1gvanyjgm";
    private static readonly string Beta = "5319275A.51895FA4EA97F_cv1g1gvanyjgm";

    private static IReadOnlyList<PackageEntry> Installed() =>
    [
        new PackageEntry(Whats, "cv1g1gvanyjgm", ["WhatsApp.Root.exe"]),
        new PackageEntry(Beta, "cv1g1gvanyjgm", ["WhatsApp.Root.exe"]),
        new PackageEntry("Other.App_aaaaaaaaaaaaa", "aaaaaaaaaaaaa", ["Other.exe"]),
    ];

    [Fact]
    public void TwinPackageOfTheSamePublisherIsReported()
    {
        var notes = AppRuleAudit.Check(["app:pkg=" + Whats], Installed(), _ => true, _ => true, _ => true);

        var note = Assert.Single(notes);
        Assert.Equal(Beta, note.Twin);
    }

    [Fact]
    public void TwinNamedByAnotherRuleIsNotReported()
    {
        var notes = AppRuleAudit.Check(["app:pkg=" + Whats, "app:pkg=" + Beta], Installed(), _ => true, _ => true, _ => true);

        Assert.Empty(notes);
    }

    [Fact]
    public void PackageFolderRuleIsReadAsItsPackage()
    {
        var rule = @"app:dir=%PROGRAMFILES%\WindowsApps\5319275A.WhatsAppDesktop_2.2628.101.0_x64__cv1g1gvanyjgm";

        var notes = AppRuleAudit.Check([rule], Installed(), _ => false, _ => false, _ => false);

        var note = Assert.Single(notes);
        Assert.Equal(Beta, note.Twin);
    }

    [Fact]
    public void PackageNoLongerInstalledIsReported()
    {
        var notes = AppRuleAudit.Check(["app:pkg=Gone.App_bbbbbbbbbbbbb"], Installed(), _ => true, _ => true, _ => true);

        var note = Assert.Single(notes);
        Assert.Null(note.Twin);
    }

    [Fact]
    public void MissingFolderFileAndServiceAreReported()
    {
        var notes = AppRuleAudit.Check([@"app:dir=%LOCALAPPDATA%\Gone", @"app:path=C:\gone\gone.exe", "app:svc=gone"], Installed(), _ => false, _ => false, _ => false);

        Assert.Equal(3, notes.Count);
        Assert.All(notes, note => Assert.Null(note.Twin));
    }

    [Fact]
    public void PresentFolderIsNotReported()
    {
        var notes = AppRuleAudit.Check([@"app:dir=%LOCALAPPDATA%\Discord"], Installed(), _ => true, _ => true, _ => true);

        Assert.Empty(notes);
    }

    [Fact]
    public void PublishersComeFromPackageRules()
    {
        var publishers = AppRuleAudit.Publishers(["app:pkg=" + Whats, @"app:dir=%LOCALAPPDATA%\Discord"]);

        Assert.Equal(["cv1g1gvanyjgm"], publishers);
    }
}
