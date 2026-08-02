using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A Store/MSIX app installs under WindowsApps into a folder whose name carries its version; matching by package
/// family (name + publisher, no version) keeps a per-app rule bound to the app across its auto-updates.
/// </summary>
public sealed class AppPathTokenTests
{
    private const string WhatsAppExe = @"C:\Program Files\WindowsApps\5319275A.WhatsAppDesktop_2.2628.101.0_x64__cv1g1gvanyjgm\WhatsApp.exe";
    private const string WhatsAppDir = @"C:\Program Files\WindowsApps\5319275A.WhatsAppDesktop_2.2628.101.0_x64__cv1g1gvanyjgm";
    private const string WhatsAppFamily = "5319275A.WhatsAppDesktop_cv1g1gvanyjgm";

    [Theory]
    [InlineData(WhatsAppExe)]
    [InlineData(WhatsAppDir)]
    [InlineData(@"%PROGRAMFILES%\WindowsApps\5319275A.WhatsAppDesktop_2.2628.101.0_x64__cv1g1gvanyjgm\WhatsApp.exe")]
    [InlineData(@"C:/Program Files/WindowsApps/5319275A.WhatsAppDesktop_2.2628.101.0_x64__cv1g1gvanyjgm/WhatsApp.exe")]
    public void PackageFamily_FromPackagedPath(string path)
    {
        Assert.Equal(WhatsAppFamily, AppPathToken.PackageFamilyFromPath(path));
    }

    [Fact]
    public void PackageFamily_IsVersionIndependent()
    {
        var before = AppPathToken.PackageFamilyFromPath(
            @"C:\Program Files\WindowsApps\5319275A.WhatsAppDesktop_2.2628.101.0_x64__cv1g1gvanyjgm\WhatsApp.exe");
        var after = AppPathToken.PackageFamilyFromPath(
            @"C:\Program Files\WindowsApps\5319275A.WhatsAppDesktop_2.2629.100.0_x64__cv1g1gvanyjgm\WhatsApp.exe");

        Assert.Equal(before, after);
        Assert.Equal(WhatsAppFamily, after);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Discord\app-1.0.9013\Discord.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\WhatsApp\WhatsApp.exe")]
    [InlineData(@"C:\Foo\WindowsApps\bar\baz.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\NotAPackageFolder\app.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Name_1.0.0.0_x64__short\app.exe")]
    [InlineData("")]
    [InlineData("   ")]
    public void PackageFamily_NullForNonPackage(string path)
    {
        Assert.Null(AppPathToken.PackageFamilyFromPath(path));
    }

    [Theory]
    [InlineData("app:dir=" + WhatsAppDir)]
    [InlineData("app:path=" + WhatsAppExe)]
    public void NormalizeAppRule_PackagedBecomesPkg(string token)
    {
        Assert.Equal("app:pkg=" + WhatsAppFamily, AppPathToken.NormalizeAppRule(token));
    }

    [Theory]
    [InlineData("app:svc=Discord")]
    [InlineData("geosite:youtube")]
    public void NormalizeAppRule_OtherKindsPassThrough(string token)
    {
        Assert.Equal(token, AppPathToken.NormalizeAppRule(token));
    }
}
