using System.Runtime.Versioning;
using AmneziaGeo.Decl;
using AmneziaGeo.Deltas;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The release tool makes one patch from the tree of the release before to the new tree: what a machine holds of the
/// earlier tree is left to it, a changed file goes as a delta where that is smaller, the rest whole. The update makes
/// the new tree from that patch and the held files.
/// </summary>
public sealed class PatchBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ag-builder-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task APatch_TurnsWhatAMachineHoldsIntoTheNewTree()
    {
        var (earlier, current) = Trees();
        var output = Path.Combine(_root, "out", "x.patch");

        var summary = await Builder().BuildAsync(Lay("tree", current), Lay("earlier", earlier), ["app"], output, CancellationToken.None);

        var entries = UpdatePatch.ReadList(output).Where(entry => entry.Sha256.Length > 0).ToDictionary(entry => entry.Path, entry => entry.Kind);
        Assert.Equal(UpdatePatchKind.Held, entries["app/a.dll"]);
        Assert.Equal(UpdatePatchKind.Held, entries["app/moved/a2.dll"]);
        Assert.Equal(UpdatePatchKind.Delta, entries["app/b.dll"]);
        Assert.Equal(UpdatePatchKind.Delta, entries["app/z.dll"]);
        Assert.Equal(UpdatePatchKind.Whole, entries["app/n.dll"]);
        Assert.Equal(UpdatePatchKind.Whole, entries["app/r.bin"]);
        Assert.Equal(UpdatePatchKind.Whole, entries["setup.exe"]);
        Assert.Equal(new PatchSummary(7, 2, 2, 3, new FileInfo(output).Length), summary);

        var target = Path.Combine(_root, "made");
        var count = await UpdatePatch.ApplyAsync(output, [Lay("installed", Installed(earlier))], target, CancellationToken.None);

        foreach (var (path, data) in current)
        {
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(target, path)));
        }

        Assert.Equal(new UpdatePatchCount(7, 5, summary.Bytes, 2), count);
        Assert.True(summary.Bytes < current.Values.Sum(data => data.Length) / 2);
    }

    [Fact]
    public async Task WithNoFolderNamed_AMachineHoldsTheWholeEarlierTree()
    {
        var (earlier, current) = Trees();
        var output = Path.Combine(_root, "out", "x.patch");

        var summary = await Builder().BuildAsync(Lay("tree", current), Lay("earlier", earlier), [], output, CancellationToken.None);

        Assert.Equal(UpdatePatchKind.Held, UpdatePatch.ReadList(output).Single(entry => entry.Path == "setup.exe").Kind);
        Assert.Equal(3, summary.Held);
    }

    [Fact]
    public async Task TwoBuildsOfTheSameTrees_GiveTheSameBytes()
    {
        var (earlier, current) = Trees();
        var tree = Lay("tree", current);
        var before = Lay("earlier", earlier);
        var first = Path.Combine(_root, "out", "first.patch");
        var second = Path.Combine(_root, "out", "second.patch");

        await Builder().BuildAsync(tree, before, ["app"], first, CancellationToken.None);
        await Builder().BuildAsync(tree, before, ["app"], second, CancellationToken.None);

        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
    }

    [Fact]
    public async Task AMachineWithAnotherVersion_IsRefusedByThePatch()
    {
        var (earlier, current) = Trees();
        var output = Path.Combine(_root, "out", "x.patch");
        await Builder().BuildAsync(Lay("tree", current), Lay("earlier", earlier), ["app"], output, CancellationToken.None);
        var other = Installed(earlier);
        other["app/b.dll"] = Words(60_000, 9);

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePatch.ApplyAsync(
            output, [Lay("installed", other)], Path.Combine(_root, "made"), CancellationToken.None));
    }

    [UnixFact("a package tree carries permissions and symbolic links")]
    [UnsupportedOSPlatform("windows")]
    public async Task ATree_CarriesItsFoldersLinksAndModes()
    {
        var tree = Lay("tree", new Dictionary<string, byte[]>
        {
            ["DEBIAN/control"] = Words(200, 1),
            ["usr/lib/app/tool"] = Words(5000, 2),
        });
        File.SetUnixFileMode(Path.Combine(tree, "usr/lib/app/tool"), (UnixFileMode)0x1ed);
        File.SetUnixFileMode(Path.Combine(tree, "DEBIAN/control"), (UnixFileMode)0x1a4);
        Directory.CreateDirectory(Path.Combine(tree, "var/lib/app"));
        File.SetUnixFileMode(Path.Combine(tree, "var/lib/app"), (UnixFileMode)0x1c0);
        Directory.CreateDirectory(Path.Combine(tree, "usr/bin"));
        File.CreateSymbolicLink(Path.Combine(tree, "usr/bin/tool"), "../lib/app/tool");
        var before = Lay("earlier", new Dictionary<string, byte[]> { ["usr/lib/app/tool"] = Words(5000, 2) });
        var output = Path.Combine(_root, "out", "x.patch");

        await Builder().BuildAsync(tree, before, ["usr/lib/app"], output, CancellationToken.None);

        var entries = UpdatePatch.ReadList(output).ToDictionary(entry => entry.Path);
        Assert.Equal(UpdatePatchKind.Folder, entries["var/lib/app"].Kind);
        Assert.Equal((UnixFileMode)0x1c0, entries["var/lib/app"].Mode);
        Assert.Equal(UpdatePatchKind.Link, entries["usr/bin/tool"].Kind);
        Assert.Equal("../lib/app/tool", entries["usr/bin/tool"].Target);
        Assert.Equal(UpdatePatchKind.Held, entries["usr/lib/app/tool"].Kind);
        Assert.Equal((UnixFileMode)0x1ed, entries["usr/lib/app/tool"].Mode);

        var target = Path.Combine(_root, "made");
        await UpdatePatch.ApplyAsync(output, [Path.Combine(before, "usr/lib/app")], target, CancellationToken.None);

        Assert.Equal((UnixFileMode)0x1ed, File.GetUnixFileMode(Path.Combine(target, "usr/lib/app/tool")));
        Assert.Equal((UnixFileMode)0x1c0, File.GetUnixFileMode(Path.Combine(target, "var/lib/app")));
        Assert.Equal("../lib/app/tool", new FileInfo(Path.Combine(target, "usr/bin/tool")).LinkTarget);
    }

    [UnixFact("a package tree carries symbolic links")]
    [UnsupportedOSPlatform("windows")]
    public async Task ALinkTheListCannotCarry_IsRefused()
    {
        var tree = Lay("tree", new Dictionary<string, byte[]> { ["usr/lib/app/tool"] = Words(5000, 2) });
        File.CreateSymbolicLink(Path.Combine(tree, "tool"), "/usr/lib/app/tool");
        var before = Lay("earlier", new Dictionary<string, byte[]> { ["usr/lib/app/tool"] = Words(5000, 2) });

        await Assert.ThrowsAsync<InvalidDataException>(() => Builder().BuildAsync(
            tree, before, [], Path.Combine(_root, "out", "x.patch"), CancellationToken.None));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static PatchBuilder Builder() => new(TimeSpan.FromMinutes(1), new StringWriter());

    // Two trees of an asset: a.dll stays and gets a copy under another name, b.dll and z.dll change a little, r.bin
    // changes and does not compress, n.dll is new, setup.exe stays but lies outside what a machine holds.
    private static (Dictionary<string, byte[]> Earlier, Dictionary<string, byte[]> Current) Trees()
    {
        var earlier = new Dictionary<string, byte[]>
        {
            ["setup.exe"] = Words(5000, 10),
            ["app/a.dll"] = Words(40_000, 1),
            ["app/b.dll"] = Words(60_000, 2),
            ["app/r.bin"] = Noise(20_000, 3),
            ["app/z.dll"] = Words(80_000, 4),
        };
        var b = (byte[])earlier["app/b.dll"].Clone();
        b[100] ^= 1;
        var z = earlier["app/z.dll"].Take(50_000).Concat(Words(300, 5)).Concat(earlier["app/z.dll"].Skip(50_000)).ToArray();
        var current = new Dictionary<string, byte[]>
        {
            ["setup.exe"] = earlier["setup.exe"],
            ["app/a.dll"] = earlier["app/a.dll"],
            ["app/moved/a2.dll"] = earlier["app/a.dll"],
            ["app/b.dll"] = b,
            ["app/n.dll"] = Words(10_000, 6),
            ["app/r.bin"] = Noise(20_000, 7),
            ["app/z.dll"] = z,
        };
        return (earlier, current);
    }

    // What a machine holds of a tree: the files under its app folder.
    private static Dictionary<string, byte[]> Installed(Dictionary<string, byte[]> tree) =>
        tree.Where(file => file.Key.StartsWith("app/", StringComparison.Ordinal)).ToDictionary(file => file.Key, file => file.Value);

    private string Lay(string name, Dictionary<string, byte[]> files)
    {
        var folder = Path.Combine(_root, name);
        foreach (var (path, data) in files)
        {
            var place = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(place)!);
            File.WriteAllBytes(place, data);
        }

        return folder;
    }

    // Data with the repeats of code: a small vocabulary of words in a random order.
    private static byte[] Words(int size, int seed)
    {
        var random = new Random(seed);
        var words = Enumerable.Range(0, 64).Select(_ => Noise(random.Next(2, 12), random.Next())).ToArray();
        var data = new List<byte>(size);
        while (data.Count < size)
        {
            data.AddRange(words[random.Next(words.Length)]);
        }

        return data.Take(size).ToArray();
    }

    private static byte[] Noise(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }
}
