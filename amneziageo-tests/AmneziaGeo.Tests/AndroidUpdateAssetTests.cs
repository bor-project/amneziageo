using System.Runtime.InteropServices;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The Android package an update fetches: the one built for the processor the app runs on, the package of every
/// processor when the release carries no such one.
/// </summary>
public sealed class AndroidUpdateAssetTests
{
    [Theory]
    [InlineData(Architecture.Arm64, "AmneziaGeo-1.9.15.0-android-arm64.apk")]
    [InlineData(Architecture.Arm, "AmneziaGeo-1.9.15.0-android-arm.apk")]
    [InlineData(Architecture.X64, "AmneziaGeo-1.9.15.0-android-x64.apk")]
    public void TakesThePackageOfTheProcessor(Architecture processor, string expected)
    {
        var manifest = Manifest(Universal, Apk("arm64"), Apk("arm"), Apk("x64"));

        Assert.Equal(expected, UpdateFeed.AndroidAsset(manifest, UpdateFeed.AndroidArch(processor))?.Name);
    }

    [Fact]
    public void AReleaseWithoutPackagesPerProcessor_GivesThePackageOfEvery()
    {
        var manifest = Manifest(Universal);

        Assert.Equal(Universal.Name, UpdateFeed.AndroidAsset(manifest, UpdateFeed.AndroidArch(Architecture.Arm64))?.Name);
    }

    [Fact]
    public void AProcessorWithoutItsOwnPackage_GetsThePackageOfEvery()
    {
        var manifest = Manifest(Universal, Apk("arm64"), Apk("arm"), Apk("x64"));

        Assert.Equal(Universal.Name, UpdateFeed.AndroidAsset(manifest, UpdateFeed.AndroidArch(Architecture.X86))?.Name);
    }

    [Fact]
    public void APackageOfAnotherPlatform_IsNeverTaken()
    {
        var manifest = Manifest(new UpdateAsset("AmneziaGeo-1.9.15.0-win-x64.exe", "windows", "x64", "scd", "00"));

        Assert.Null(UpdateFeed.AndroidAsset(manifest, UpdateFeed.AndroidArch(Architecture.X64)));
    }

    [Fact]
    public void ReadsThePackagesFromThePublishedManifest()
    {
        var manifest = UpdateFeed.ParseManifest("""
            {"version":"1.9.15.0","description":"AmneziaGeo 1.9.15.0","channel":"prerelease","setup":"","installers":[
              {"name":"AmneziaGeo-1.9.15.0-android.apk","platform":"android","arch":"universal","variant":"apk","sha256":"aa"},
              {"name":"AmneziaGeo-1.9.15.0-android-arm64.apk","platform":"android","arch":"arm64","variant":"apk","sha256":"bb"}]}
            """)!;

        var asset = UpdateFeed.AndroidAsset(manifest, "arm64");

        Assert.Equal(("AmneziaGeo-1.9.15.0-android-arm64.apk", "bb"), (asset?.Name, asset?.Sha256));
    }

    private static UpdateAsset Universal => new("AmneziaGeo-1.9.15.0-android.apk", "android", "universal", "apk", "00");

    private static UpdateAsset Apk(string arch) => new($"AmneziaGeo-1.9.15.0-android-{arch}.apk", "android", arch, "apk", "00");

    private static UpdateManifest Manifest(params UpdateAsset[] installers) =>
        new("1.9.15.0", "AmneziaGeo 1.9.15.0", "prerelease", string.Empty, installers);
}
