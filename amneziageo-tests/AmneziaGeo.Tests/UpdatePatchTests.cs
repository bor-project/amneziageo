using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// An update puts the asset of a release together from the files the machine holds and the patch beside the asset:
/// one archive whose list names every file as held, carried whole or made from a delta. Whatever does not hold
/// together is refused, so the update falls back to the whole asset.
/// </summary>
public sealed class UpdatePatchTests : IDisposable
{
    private const string Setup = "AmneziaGeoSetup.exe";
    private const string Msi = "AmneziaGeo-win-x64.msi";
    private const string App = "PFiles64/AmneziaGeo/";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ag-patch-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task APatch_MakesTheAssetFromHeldFilesItsOwnAndADelta()
    {
        var plan = Layout();
        var patch = plan.Archive();
        var feed = new Feed(patch);
        var target = Path.Combine(_root, "layout");
        var progress = new List<(long Done, long Total)>();

        var count = await UpdatePatch.FetchAsync(
            new HttpClient(feed), Address, Named(patch), [Hold(plan)], target, (done, total) => progress.Add((done, total)), CancellationToken.None);

        foreach (var (path, data) in plan.Files)
        {
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(target, path)));
        }

        Assert.Equal(new UpdatePatchCount(6, 4, patch.Length, 1), count);
        Assert.Equal(new[] { "x.patch" }, feed.Asked);
        Assert.Equal(patch, await File.ReadAllBytesAsync(target + UpdatePatch.Extension));
        Assert.Equal(patch.Length, progress[^1].Done);
        Assert.Equal(patch.Length, progress[^1].Total);
    }

    [Fact]
    public async Task AMachineThatLacksAHeldFile_IsRefused()
    {
        var plan = Layout();
        var patch = plan.Archive();
        plan.Machine.RemoveAt(0);
        var target = Path.Combine(_root, "layout");

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePatch.FetchAsync(
            new HttpClient(new Feed(patch)), Address, Named(patch), [Hold(plan)], target, null, CancellationToken.None));

        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task AMachineThatLacksTheEarlierFileOfADelta_IsRefused()
    {
        var plan = Layout();
        var patch = plan.Archive();
        plan.Machine.RemoveAt(plan.Machine.Count - 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePatch.FetchAsync(
            new HttpClient(new Feed(patch)), Address, Named(patch), [Hold(plan)], Path.Combine(_root, "layout"), null, CancellationToken.None));
    }

    [Fact]
    public async Task APatchUnlikeTheManifestOne_IsRefusedAndDropped()
    {
        var plan = Layout();
        var patch = plan.Archive();
        var target = Path.Combine(_root, "layout");
        var named = Named(patch) with { Sha256 = new string('0', 64) };

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePatch.FetchAsync(
            new HttpClient(new Feed(patch)), Address, named, [Hold(plan)], target, null, CancellationToken.None));

        Assert.False(File.Exists(target + UpdatePatch.Extension));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task AFileUnlikeItsDigest_IsRefused()
    {
        var plan = Layout();
        var data = Bytes(700, 20);
        plan.Line($"+ {Sha(data)} 644 {data.Length} {App}odd.dll");
        plan.Bodies.Add((UpdatePatch.WholeFolder + Sha(data), Bytes(700, 21)));
        var patch = plan.Archive();

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePatch.FetchAsync(
            new HttpClient(new Feed(patch)), Address, Named(patch), [Hold(plan)], Path.Combine(_root, "layout"), null, CancellationToken.None));
    }

    [Fact]
    public async Task AFileOfAnotherSizeThanItsLine_IsRefused()
    {
        var plan = Layout();
        var data = Bytes(700, 20);
        plan.Line($"+ {Sha(data)} 644 {data.Length} {App}odd.dll");
        plan.Bodies.Add((UpdatePatch.WholeFolder + Sha(data), Bytes(900, 21)));
        var patch = plan.Archive();

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePatch.FetchAsync(
            new HttpClient(new Feed(patch)), Address, Named(patch), [Hold(plan)], Path.Combine(_root, "layout"), null, CancellationToken.None));
    }

    [Fact]
    public async Task ACorruptDelta_IsRefused()
    {
        var plan = Layout();
        var (name, delta) = plan.Bodies[^1];
        var broken = (byte[])delta.Clone();
        broken[broken.Length / 2] ^= 0xff;
        plan.Bodies[^1] = (name, broken);
        var patch = plan.Archive();

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePatch.FetchAsync(
            new HttpClient(new Feed(patch)), Address, Named(patch), [Hold(plan)], Path.Combine(_root, "layout"), null, CancellationToken.None));
    }

    [Fact]
    public async Task AFileTheListNamesButThePatchLacks_IsRefused()
    {
        var plan = Layout();
        plan.Bodies.RemoveAt(0);
        var patch = plan.Archive();

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePatch.FetchAsync(
            new HttpClient(new Feed(patch)), Address, Named(patch), [Hold(plan)], Path.Combine(_root, "layout"), null, CancellationToken.None));
    }

    [Fact]
    public async Task WhatIsNoArchive_IsRefused()
    {
        var patch = Bytes(4000, 30);

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePatch.FetchAsync(
            new HttpClient(new Feed(patch)), Address, Named(patch), [], Path.Combine(_root, "layout"), null, CancellationToken.None));
    }

    [Fact]
    public async Task APatchOnDisk_IsNotDownloadedAgain()
    {
        var plan = Layout();
        var patch = plan.Archive();
        var feed = new Feed(patch);
        var target = Path.Combine(_root, "layout");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(target + UpdatePatch.Extension, patch);

        var count = await UpdatePatch.FetchAsync(new HttpClient(feed), Address, Named(patch), [Hold(plan)], target, null, CancellationToken.None);

        Assert.Equal(6, count.Files);
        Assert.Empty(feed.Asked);
    }

    [Fact]
    public async Task ABrokenDownload_CarriesOnFromItsPartial()
    {
        var plan = Layout();
        var patch = plan.Archive();
        var feed = new Feed(patch);
        var target = Path.Combine(_root, "layout");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(target + UpdatePatch.Extension + ".part", patch[..100]);

        var count = await UpdatePatch.FetchAsync(new HttpClient(feed), Address, Named(patch), [Hold(plan)], target, null, CancellationToken.None);

        Assert.Equal(6, count.Files);
        Assert.Equal(new[] { "x.patch 100-" }, feed.Asked);
    }

    [Fact]
    public async Task WhatAnEarlierAttemptLeftInTheFolder_IsReplaced()
    {
        var plan = Layout();
        var patch = plan.Archive();
        var target = Path.Combine(_root, "layout");
        Directory.CreateDirectory(Path.Combine(target, "PFiles64"));
        File.WriteAllBytes(Path.Combine(target, "PFiles64", "stale.dll"), [1]);

        await UpdatePatch.FetchAsync(new HttpClient(new Feed(patch)), Address, Named(patch), [Hold(plan)], target, null, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(target, "PFiles64", "stale.dll")));
        Assert.True(File.Exists(Path.Combine(target, Setup)));
    }

    [UnixFact("a package tree carries permissions and symbolic links")]
    [UnsupportedOSPlatform("windows")]
    public async Task FoldersLinksAndModes_ArePutInPlace()
    {
        var plan = new Plan()
            .Whole("DEBIAN/control", Encoding.UTF8.GetBytes("Package: amneziageo\n"))
            .Whole("DEBIAN/postinst", Encoding.UTF8.GetBytes("#!/bin/sh\n"), "755")
            .Whole("usr/lib/amneziageo/amneziageo", Bytes(3000, 1), "755")
            .Line("d 755 DEBIAN")
            .Line("d 755 usr")
            .Line("d 755 usr/bin")
            .Line("d 755 usr/lib")
            .Line("d 755 usr/lib/amneziageo")
            .Line("d 755 var")
            .Line("d 755 var/lib")
            .Line("d 700 var/lib/amneziageo")
            .Line("l ../lib/amneziageo/amneziageo usr/bin/amneziageo");
        var path = Path.Combine(_root, "x.patch");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(path, plan.Archive());
        var target = Path.Combine(_root, "tree");

        await UpdatePatch.ApplyAsync(path, [], target, CancellationToken.None);

        const UnixFileMode Executable = (UnixFileMode)0x1ed;
        Assert.Equal(Executable, File.GetUnixFileMode(Path.Combine(target, "usr/lib/amneziageo/amneziageo")));
        Assert.Equal((UnixFileMode)0x1a4, File.GetUnixFileMode(Path.Combine(target, "DEBIAN/control")));
        Assert.Equal((UnixFileMode)0x1c0, File.GetUnixFileMode(Path.Combine(target, "var/lib/amneziageo")));
        Assert.Equal("../lib/amneziageo/amneziageo", new FileInfo(Path.Combine(target, "usr/bin/amneziageo")).LinkTarget);
    }

    [Fact]
    public void AList_ReadsEveryKindOfEntryAndWritesItBack()
    {
        var a = new string('a', 64);
        var b = new string('b', 64);
        var lines = new[]
        {
            UpdatePatch.Head,
            $"= {a} 644 10 app/kept one.dll",
            $"+ {b} 755 20 app/new.dll",
            $"~ {new string('c', 64)} 644 30 {a} 10 app/changed.dll",
            "d 700 var/lib/app",
            "l ../lib/app/tool usr/bin/tool",
        };

        var entries = UpdatePatch.Parse(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));

        Assert.Equal(new UpdatePatchEntry(UpdatePatchKind.Held, "app/kept one.dll", (UnixFileMode)0x1a4, a, 10), entries[0]);
        Assert.Equal(new UpdatePatchEntry(UpdatePatchKind.Whole, "app/new.dll", (UnixFileMode)0x1ed, b, 20), entries[1]);
        Assert.Equal(new UpdatePatchEntry(UpdatePatchKind.Delta, "app/changed.dll", (UnixFileMode)0x1a4, new string('c', 64), 30, a, 10), entries[2]);
        Assert.Equal(new UpdatePatchEntry(UpdatePatchKind.Folder, "var/lib/app", (UnixFileMode)0x1c0), entries[3]);
        Assert.Equal(new UpdatePatchEntry(UpdatePatchKind.Link, "usr/bin/tool", (UnixFileMode)0x1ff, Target: "../lib/app/tool"), entries[4]);
        Assert.Equal(lines.Skip(1), entries.Select(UpdatePatch.Line));
    }

    [Theory]
    [MemberData(nameof(BrokenLists))]
    public void AListThatDoesNotHoldTogether_IsRefused(string why, string list)
    {
        Assert.False(string.IsNullOrEmpty(why));
        Assert.Throws<InvalidDataException>(() => UpdatePatch.Parse(Encoding.UTF8.GetBytes(list)));
    }

    public static TheoryData<string, string> BrokenLists()
    {
        var a = new string('a', 64);
        var b = new string('b', 64);
        var head = UpdatePatch.Head + "\n";
        return new TheoryData<string, string>
        {
            { "no head", $"+ {a} 644 1 x\n" },
            { "the head of another list", $"# amneziageo files 1\n+ {a} 644 1 x\n" },
            { "no file", head + "d 755 x\n" },
            { "an unknown kind", head + $"? {a} 644 1 x\n" },
            { "a short digest", head + "+ abc 644 1 x\n" },
            { "a digest in capitals", head + $"+ {a.ToUpperInvariant()} 644 1 x\n" },
            { "a mode that is none", head + $"+ {a} 9x9 1 x\n" },
            { "a size that is no number", head + $"+ {a} 644 -1 x\n" },
            { "a path up the tree", head + $"+ {a} 644 1 ../x\n" },
            { "a path from the root", head + $"+ {a} 644 1 /etc/x\n" },
            { "a path with a drive", head + $"+ {a} 644 1 C:/x\n" },
            { "a path with a backslash", head + $"+ {a} 644 1 a\\x\n" },
            { "a path twice", head + $"+ {a} 644 1 x\n= {b} 644 1 x\n" },
            { "a delta from itself", head + $"~ {a} 644 1 {a} 1 x\n" },
            { "a delta without its earlier size", head + $"~ {a} 644 1 {b} x\n" },
            { "one file made two ways", head + $"+ {a} 644 1 x\n= {a} 644 1 y\n" },
            { "one file made from two earlier ones", head + $"~ {a} 644 1 {b} 1 x\n~ {a} 644 1 {new string('c', 64)} 1 y\n" },
            { "a path under a link", head + $"+ {a} 644 1 l/x\nl target l\n" },
            { "a link from the root", head + $"+ {a} 644 1 x\nl /etc/passwd y\n" },
        };
    }

    [Fact]
    public void TheManifest_NamesThePatchOfAnAsset()
    {
        const string Json = """
            {"version":"1.10.8.0","installers":[
              {"name":"AmneziaGeo-1.10.8.0-win-x64-fdd.exe","platform":"windows","arch":"x64","variant":"fdd","sha256":"ab","size":45000000,
               "patch":{"name":"AmneziaGeo-1.10.8.0-win-x64-fdd.patch","size":2300000,"sha256":"cd","from":"1.10.7.0"}},
              {"name":"AmneziaGeo-1.10.8.0-android.apk","platform":"android","arch":"universal","variant":"apk","sha256":"ef"}]}
            """;

        var manifest = UpdateFeed.ParseManifest(Json)!;
        var asset = UpdateFeed.AssetNamed(manifest, "AmneziaGeo-1.10.8.0-win-x64-fdd.exe")!;

        Assert.Equal(45000000, asset.Size);
        Assert.Equal(new UpdatePatchFile("AmneziaGeo-1.10.8.0-win-x64-fdd.patch", 2300000, "cd", "1.10.7.0"), asset.Patch);
        Assert.Null(UpdateFeed.AssetNamed(manifest, "AmneziaGeo-1.10.8.0-android.apk")!.Patch);
    }

    [Theory]
    [InlineData("1.10.7.0", true)]
    [InlineData("1.10.7", true)]
    [InlineData("1.10.6.0", false)]
    [InlineData("1.10.7.1", false)]
    [InlineData("1.10.8.0", false)]
    [InlineData("", false)]
    public void APatch_IsTakenOnlyByTheVersionItStartsFrom(string installed, bool taken)
    {
        var patch = new UpdatePatchFile("x.patch", 100, new string('a', 64), "1.10.7.0");
        var asset = new UpdateAsset("x.exe", "windows", "x64", "fdd", "ab", 1000, patch);

        Assert.Equal(taken ? patch : null, UpdateFeed.PatchFor(asset, installed));
    }

    [Fact]
    public void APatchTheManifestNamesInPart_IsNotTaken()
    {
        var patch = new UpdatePatchFile("x.patch", 100, new string('a', 64), "1.10.7.0");

        Assert.Null(UpdateFeed.PatchFor(null, "1.10.7.0"));
        Assert.Null(UpdateFeed.PatchFor(new UpdateAsset("x.exe", "windows", "x64", "fdd", "ab", 1000), "1.10.7.0"));
        Assert.Null(UpdateFeed.PatchFor(new UpdateAsset("x.exe", "windows", "x64", "fdd", "ab", 1000, patch with { Sha256 = null }), "1.10.7.0"));
        Assert.Null(UpdateFeed.PatchFor(new UpdateAsset("x.exe", "windows", "x64", "fdd", "ab", 1000, patch with { Size = 0 }), "1.10.7.0"));
        Assert.Null(UpdateFeed.PatchFor(new UpdateAsset("x.exe", "windows", "x64", "fdd", "ab", 1000, patch with { Name = "" }), "1.10.7.0"));
        Assert.Null(UpdateFeed.PatchFor(new UpdateAsset("x.exe", "windows", "x64", "fdd", "ab", 1000, patch with { From = "soon" }), "1.10.7.0"));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Uri Address { get; } = new("https://example.test/releases/download/v9.9.9.9/x.patch");

    private static UpdatePatchFile Named(byte[] patch) => new("x.patch", patch.Length, Sha(patch), "9.9.9.8");

    // The update layout of a Windows setup: the bundle and the MSI come whole, two runtime files and a copy of one
    // of them are held, one of ours comes as a delta.
    private static Plan Layout()
    {
        var earlier = Words(64 * 1024, 7);
        var current = (byte[])earlier.Clone();
        current[100] ^= 1;
        current[40_000] ^= 2;
        var kept = Bytes(3000, 3);
        return new Plan()
            .Whole(Msi, Bytes(1024, 1))
            .Whole(Setup, Bytes(2048, 2))
            .Held(App + "a.dll", kept)
            .Held(App + "copy/a.dll", kept)
            .Whole(App + "new.dll", Bytes(1500, 5))
            .Delta(App + "z.dll", earlier, current);
    }

    // Lays what the machine holds out the way an installed copy does: under names of its own.
    private string Hold(Plan plan)
    {
        var held = Path.Combine(_root, "installed");
        Directory.CreateDirectory(held);
        for (var i = 0; i < plan.Machine.Count; i++)
        {
            File.WriteAllBytes(Path.Combine(held, $"{i}.held"), plan.Machine[i]);
        }

        return held;
    }

    // Data with the repeats of code: a small vocabulary of words in a random order.
    private static byte[] Words(int size, int seed)
    {
        var random = new Random(seed);
        var words = Enumerable.Range(0, 64).Select(_ => Bytes(random.Next(2, 12), random.Next())).ToArray();
        var data = new List<byte>(size);
        while (data.Count < size)
        {
            data.AddRange(words[random.Next(words.Length)]);
        }

        return data.Take(size).ToArray();
    }

    private static byte[] Bytes(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    // A patch in the making: the lines of its list, its bodies, the files it stands for and what the machine holds.
    private sealed class Plan
    {
        public List<string> Lines { get; } = [UpdatePatch.Head];

        public List<(string Name, byte[] Data)> Bodies { get; } = [];

        public List<(string Path, byte[] Data)> Files { get; } = [];

        public List<byte[]> Machine { get; } = [];

        public Plan Held(string path, byte[] data)
        {
            Lines.Add($"= {Sha(data)} 644 {data.Length} {path}");
            Files.Add((path, data));
            if (!Machine.Contains(data))
            {
                Machine.Add(data);
            }

            return this;
        }

        public Plan Whole(string path, byte[] data, string mode = "644")
        {
            Lines.Add($"+ {Sha(data)} {mode} {data.Length} {path}");
            Bodies.Add((UpdatePatch.WholeFolder + Sha(data), data));
            Files.Add((path, data));
            return this;
        }

        public Plan Delta(string path, byte[] earlier, byte[] current)
        {
            Lines.Add($"~ {Sha(current)} 644 {current.Length} {Sha(earlier)} {earlier.Length} {path}");
            Bodies.Add((UpdatePatch.DeltaFolder + Sha(current), UpdateDelta.Create(earlier, current)));
            Files.Add((path, current));
            Machine.Add(earlier);
            return this;
        }

        public Plan Line(string line)
        {
            Lines.Add(line);
            return this;
        }

        public byte[] Archive()
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add(archive, UpdatePatch.ListName, Encoding.UTF8.GetBytes(string.Join('\n', Lines) + "\n"));
                foreach (var (name, data) in Bodies)
                {
                    Add(archive, name, data);
                }
            }

            return output.ToArray();
        }

        private static void Add(ZipArchive archive, string name, byte[] data)
        {
            using var entry = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
            entry.Write(data);
        }
    }

    // A release server: the patch whole or from the byte asked for.
    private sealed class Feed(byte[] patch) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = request.RequestUri!.Segments[^1];
            var range = request.Headers.Range?.Ranges.First();
            Asked.Add(range is null ? name : $"{name} {range.From}-{range.To}");
            if (range is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(patch) });
            }

            var from = range.From ?? 0;
            var to = range.To ?? patch.Length - 1;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(patch[(int)from..(int)(to + 1)]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, patch.Length);
            return Task.FromResult(response);
        }
    }
}
