using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AmneziaGeo.Decl;
using AmneziaGeo.Ui.Services;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The Windows window keeps the setup it downloads in a folder of its own: the whole setup, or the update layout put
/// together from the installed copy and the patch of the release. Only the downloads of the setup on offer stay, and
/// the setup of a layout is checked against the list of that patch, which the manifest vouches for.
/// </summary>
public sealed class SetupDownloadsTests : IDisposable
{
    private const string Offered = "AmneziaGeo-1.9.15.0-win-x64-fdd.exe";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ag-setup-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// ctor
    /// </summary>
    public SetupDownloadsTests()
    {
        Directory.CreateDirectory(_folder);
    }

    [Fact]
    public void ASweep_KeepsOnlyTheSetupOnOffer()
    {
        Touch(Offered + ".part");
        Touch("AmneziaGeo-1.9.15.0-win-x64-fdd.patch");
        Directory.CreateDirectory(Path.Combine(_folder, "AmneziaGeo-1.9.15.0-win-x64-fdd"));
        Touch("AmneziaGeo-1.9.14.0-win-x64-fdd.exe");
        Touch("AmneziaGeo-1.9.14.0-win-x64-fdd.patch.part");
        Touch("AmneziaGeo-1.9.15.0-win-x64.exe.part");
        Directory.CreateDirectory(Path.Combine(_folder, "AmneziaGeo-1.9.14.0-win-x64-fdd"));

        SetupDownloads.Sweep(_folder, Offered);

        Assert.Equal(
            new[] { "AmneziaGeo-1.9.15.0-win-x64-fdd", "AmneziaGeo-1.9.15.0-win-x64-fdd.exe.part", "AmneziaGeo-1.9.15.0-win-x64-fdd.patch" },
            Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ADrop_TakesTheSetupOnOfferAway()
    {
        Touch(Offered + ".part");
        Touch("AmneziaGeo-1.9.15.0-win-x64-fdd.patch");
        Touch("AmneziaGeo-1.9.15.0-win-x64-fdd.patch.part");
        Directory.CreateDirectory(Path.Combine(_folder, "AmneziaGeo-1.9.15.0-win-x64-fdd", "PFiles64"));

        SetupDownloads.Drop(_folder, Offered);

        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder));
    }

    [Fact]
    public void DownloadsOfTheVersionRunning_AndEarlier_AreDropped()
    {
        Touch("AmneziaGeo-1.9.14.3-win-x64-fdd.exe");
        Touch("AmneziaGeo-1.9.14.3-win-x64-fdd.patch");
        Directory.CreateDirectory(Path.Combine(_folder, "AmneziaGeo-1.9.14.3-win-x64-fdd", "PFiles64"));
        Touch("AmneziaGeo-1.9.14.2-win-x64-fdd.exe.part");
        Touch(Offered + ".part");
        Touch("notes.txt");

        SetupDownloads.DropInstalled(_folder, new Version(1, 9, 14, 3));

        Assert.Equal(
            new[] { "AmneziaGeo-1.9.15.0-win-x64-fdd.exe.part", "notes.txt" },
            Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ADroppedLayout_LeavesTheWholeSetup()
    {
        Touch(Offered);
        Touch("AmneziaGeo-1.9.15.0-win-x64-fdd.patch");
        Touch("AmneziaGeo-1.9.15.0-win-x64-fdd.patch.part");
        Directory.CreateDirectory(Path.Combine(_folder, "AmneziaGeo-1.9.15.0-win-x64-fdd", "PFiles64"));

        SetupDownloads.DropLayout(_folder, Offered);

        Assert.Equal(new[] { Offered }, Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void TheWholeSetup_IsCheckedAgainstThePublishedDigest()
    {
        var path = Touch(Offered);

        Assert.Equal("ab", SetupDownloads.DigestFor(path, null, "ab"));
    }

    [Fact]
    public void TheSetupOfALayout_IsCheckedAgainstTheListOfItsPatch()
    {
        var (setup, asset, digest) = Layout();

        Assert.Equal(digest, SetupDownloads.DigestFor(setup, asset, "ab"));
    }

    [Fact]
    public void ALayoutWhosePatchTheManifestDoesNotVouchFor_MatchesNothing()
    {
        var (setup, asset, digest) = Layout();
        asset = asset with { Patch = asset.Patch! with { Sha256 = new string('0', 64) } };

        var expected = SetupDownloads.DigestFor(setup, asset, "ab");

        Assert.NotEqual(digest, expected);
        Assert.NotEqual("ab", expected);
        Assert.NotEmpty(expected);
    }

    [Fact]
    public void ALayoutWithoutTheManifest_MatchesNothing()
    {
        var (setup, _, digest) = Layout();

        var expected = SetupDownloads.DigestFor(setup, null, "ab");

        Assert.NotEqual(digest, expected);
        Assert.NotEmpty(expected);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    private (string Setup, UpdateAsset Asset, string Digest) Layout()
    {
        var layout = Path.Combine(_folder, "AmneziaGeo-1.9.15.0-win-x64-fdd");
        Directory.CreateDirectory(layout);
        var setup = Path.Combine(layout, SetupDownloads.LayoutSetup);
        File.WriteAllBytes(setup, [1, 2, 3]);
        var digest = new string('c', 64);
        var list = Encoding.UTF8.GetBytes(
            $"{UpdatePatch.Head}\n+ {new string('d', 64)} 644 9 AmneziaGeo-win-x64.msi\n+ {digest} 644 3 {SetupDownloads.LayoutSetup}\n");
        using (var file = File.Create(layout + UpdatePatch.Extension))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry(UpdatePatch.ListName).Open())
        {
            entry.Write(list);
        }

        var patch = File.ReadAllBytes(layout + UpdatePatch.Extension);
        var asset = new UpdateAsset(
            Offered,
            "windows",
            "x64",
            "fdd",
            "ab",
            45_000_000,
            new UpdatePatchFile("AmneziaGeo-1.9.15.0-win-x64-fdd.patch", patch.Length, Convert.ToHexStringLower(SHA256.HashData(patch)), "1.9.14.0"));
        return (setup, asset, digest);
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, [0]);
        return path;
    }
}
