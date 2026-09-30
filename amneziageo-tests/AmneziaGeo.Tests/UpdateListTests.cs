using System.Text;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The list beside a release asset names every file of the asset, where its packed copy lies in the pack, and on
/// Linux the folders and links of the package too. A list that does not hold together is refused, so an update
/// never puts together a tree the release does not describe.
/// </summary>
public sealed class UpdateListTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void AList_NamesFilesFoldersAndLinks()
    {
        var entries = UpdateList.Parse(
            Text(
                $"{A} 755 10 0 7 usr/lib/amneziageo/amneziageo",
                $"{B} 644 3 7 5 usr/lib/amneziageo/a.json",
                "d 700 var/lib/amneziageo",
                "l ../lib/amneziageo/amneziageo usr/bin/amneziageo"),
            12);

        Assert.Equal(4, entries.Count);
        Assert.Equal(new UpdateEntry(UpdateEntryKind.File, "usr/lib/amneziageo/amneziageo", (UnixFileMode)0x1ed, A, 10, 0, 7), entries[0]);
        Assert.Equal(7, entries[1].Offset);
        Assert.Equal(UpdateEntryKind.Folder, entries[2].Kind);
        Assert.Equal((UnixFileMode)0x1c0, entries[2].Mode);
        Assert.Equal(UpdateEntryKind.Link, entries[3].Kind);
        Assert.Equal("../lib/amneziageo/amneziageo", entries[3].Target);
        Assert.Equal("usr/bin/amneziageo", entries[3].Path);
    }

    [Fact]
    public void AList_WithoutItsHead_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => UpdateList.Parse(Encoding.UTF8.GetBytes($"{A} 644 1 0 1 a\n"), 1));
    }

    [Fact]
    public void AList_OfTheServer_IsRefused()
    {
        var text = Encoding.UTF8.GetBytes($"# amneziageo-server files 1\n{A} 644 1 0 1 a\n");

        Assert.Throws<InvalidDataException>(() => UpdateList.Parse(text, 1));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(3L)]
    public void AFile_ThatDoesNotFollowTheOneBefore_IsRefused(long offset)
    {
        var text = Text($"{A} 644 1 0 2 a", $"{B} 644 1 {offset} 2 b");

        Assert.Throws<InvalidDataException>(() => UpdateList.Parse(text, offset + 2));
    }

    [Fact]
    public void AList_ThatDoesNotCoverThePack_IsRefused()
    {
        var text = Text($"{A} 644 1 0 2 a");

        Assert.Throws<InvalidDataException>(() => UpdateList.Parse(text, 3));
    }

    [Fact]
    public void APath_NamedTwice_IsRefused()
    {
        var text = Text($"{A} 644 1 0 2 a", "d 755 a");

        Assert.Throws<InvalidDataException>(() => UpdateList.Parse(text, 2));
    }

    [Theory]
    [InlineData("../a")]
    [InlineData("/etc/a")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("a\\b")]
    [InlineData("C:/a")]
    public void APath_OutsideTheTree_IsRefused(string path)
    {
        var text = Text($"{A} 644 1 0 2 {path}");

        Assert.Throws<InvalidDataException>(() => UpdateList.Parse(text, 2));
    }

    [Fact]
    public void AnEntry_UnderALink_IsRefused()
    {
        var text = Text("l ../etc usr/x", $"{A} 644 1 0 2 usr/x/passwd");

        Assert.Throws<InvalidDataException>(() => UpdateList.Parse(text, 2));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public void ALink_ToAnAbsoluteOrEmptyTarget_IsRefused(string target)
    {
        var text = Text($"l {target} usr/bin/amneziageo", $"{A} 644 1 0 2 a");

        Assert.Throws<InvalidDataException>(() => UpdateList.Parse(text, 2));
    }

    [Theory]
    [InlineData("zz 644 1 0 2 a")]
    [InlineData("{0} 999 1 0 2 a")]
    [InlineData("{0} 644 -1 0 2 a")]
    [InlineData("{0} 644 1 0 0 a")]
    [InlineData("{0} 644 1 0 2")]
    [InlineData("d 7777x a")]
    [InlineData("x 644 a")]
    public void ALine_ThatDoesNotRead_IsRefused(string line)
    {
        var text = Text(string.Format(System.Globalization.CultureInfo.InvariantCulture, line, A));

        Assert.Throws<InvalidDataException>(() => UpdateList.Parse(text, 2));
    }

    [Fact]
    public void AFile_LargerThanAnUpdateTakes_IsRefused()
    {
        var text = Text($"{A} 644 {UpdateList.MaxFileSize + 1} 0 2 a");

        Assert.Throws<InvalidDataException>(() => UpdateList.Parse(text, 2));
    }

    [Fact]
    public void AList_WithoutFiles_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => UpdateList.Parse(Text("d 755 a"), 0));
    }

    [Fact]
    public void AList_TakesAPathWithSpaces()
    {
        var entries = UpdateList.Parse(Text($"{A} 644 1 0 2 PFiles64/AmneziaGeo/a b.dll"), 2);

        Assert.Equal("PFiles64/AmneziaGeo/a b.dll", entries[0].Path);
    }

    private static byte[] Text(params string[] lines) =>
        Encoding.UTF8.GetBytes(UpdateList.Head + "\n" + string.Join('\n', lines) + "\n");
}
