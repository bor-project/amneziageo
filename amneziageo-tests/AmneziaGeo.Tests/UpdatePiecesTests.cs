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
/// An update puts the asset of a release together from the files the machine already holds and fetches only the
/// files it lacks, in parts of the pack beside the asset. Whatever does not hold together is refused, so the update
/// falls back to the whole asset.
/// </summary>
public sealed class UpdatePiecesTests : IDisposable
{
    private const string Setup = "AmneziaGeoSetup.exe";
    private const string Msi = "AmneziaGeo-win-x64.msi";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ag-pieces-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ATree_ComesTogetherFromHeldFilesAndThePack()
    {
        var files = Layout(heldSize: 2048);
        var (list, pack) = Pack(files);
        var feed = new Feed(list, pack);
        var held = Hold(files, "PFiles64/AmneziaGeo/a.dll", "PFiles64/AmneziaGeo/b.dll", "PFiles64/AmneziaGeo/runtimes/c.dll");
        var target = Path.Combine(_root, "layout");
        var progress = new List<(long Done, long Total)>();

        var count = await new UpdatePieces(new HttpClient(feed)).StageAsync(
            Release, Asset(list, pack, 100_000_000), [held], target, (done, total) => progress.Add((done, total)), CancellationToken.None);

        foreach (var (path, data) in files)
        {
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(target, path)));
        }

        Assert.Equal(new UpdatePieceCount(6, 3, count.Bytes), count);
        Assert.Equal(new[] { "x.files", "x.pack" }, feed.Asked.Select(asked => asked.Split(' ')[0]));
        Assert.Equal(list, await File.ReadAllBytesAsync(target + ".files"));
        Assert.Equal(count.Bytes, progress[^1].Done);
        Assert.Equal(count.Bytes, progress[^1].Total);
    }

    [Fact]
    public async Task FilesFarApart_ComeInPartsOfTheirOwn()
    {
        var files = Layout(heldSize: 400 * 1024);
        var (list, pack) = Pack(files);
        var feed = new Feed(list, pack);
        var held = Hold(files, "PFiles64/AmneziaGeo/a.dll", "PFiles64/AmneziaGeo/b.dll", "PFiles64/AmneziaGeo/runtimes/c.dll");

        var count = await new UpdatePieces(new HttpClient(feed)).StageAsync(
            Release, Asset(list, pack, 100_000_000), [held], Path.Combine(_root, "layout"), null, CancellationToken.None);

        Assert.Equal(3, count.Fetched);
        Assert.Equal(2, feed.Asked.Count(asked => asked.StartsWith("x.pack ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MostOfTheAssetChanged_IsRefused()
    {
        var files = Layout(heldSize: 2048);
        var (list, pack) = Pack(files);
        var feed = new Feed(list, pack);

        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdatePieces(new HttpClient(feed)).StageAsync(
            Release, Asset(list, pack, pack.Length), [], Path.Combine(_root, "layout"), null, CancellationToken.None));

        Assert.Equal(new[] { "x.files" }, feed.Asked);
    }

    [Fact]
    public async Task AServerThatIgnoresRanges_IsRefused()
    {
        var files = Layout(heldSize: 2048);
        var (list, pack) = Pack(files);
        var feed = new Feed(list, pack) { Ranges = false };
        var held = Hold(files, "PFiles64/AmneziaGeo/a.dll");

        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdatePieces(new HttpClient(feed)).StageAsync(
            Release, Asset(list, pack, 100_000_000), [held], Path.Combine(_root, "layout"), null, CancellationToken.None));
    }

    [Fact]
    public async Task ACorruptPart_IsRefused()
    {
        var files = Layout(heldSize: 2048);
        var (list, pack) = Pack(files);
        pack[30] ^= 0xff;
        var feed = new Feed(list, pack);
        var held = Hold(files, "PFiles64/AmneziaGeo/a.dll");

        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdatePieces(new HttpClient(feed)).StageAsync(
            Release, Asset(list, pack, 100_000_000), [held], Path.Combine(_root, "layout"), null, CancellationToken.None));
    }

    [Fact]
    public async Task AListUnlikeTheManifestOne_IsRefused()
    {
        var files = Layout(heldSize: 2048);
        var (list, pack) = Pack(files);
        var feed = new Feed(list, pack);
        var asset = Asset(list, pack, 100_000_000);
        asset = asset with { Files = asset.Files! with { Sha256 = new string('0', 64) } };

        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdatePieces(new HttpClient(feed)).StageAsync(
            Release, asset, [], Path.Combine(_root, "layout"), null, CancellationToken.None));

        Assert.Equal(new[] { "x.files" }, feed.Asked);
    }

    [Fact]
    public async Task AnEarlierAttempt_IsTakenUp()
    {
        var files = Layout(heldSize: 400 * 1024);
        var (list, pack) = Pack(files);
        var held = Hold(files, "PFiles64/AmneziaGeo/a.dll", "PFiles64/AmneziaGeo/b.dll", "PFiles64/AmneziaGeo/runtimes/c.dll");
        var target = Path.Combine(_root, "layout");
        var asset = Asset(list, pack, 100_000_000);

        await Assert.ThrowsAsync<HttpRequestException>(() => new UpdatePieces(new HttpClient(new Feed(list, pack) { PartsBeforeDrop = 1 }))
            .StageAsync(Release, asset, [held], target, null, CancellationToken.None));

        var feed = new Feed(list, pack);
        var count = await new UpdatePieces(new HttpClient(feed)).StageAsync(Release, asset, [held], target, null, CancellationToken.None);

        Assert.Equal(1, count.Fetched);
        Assert.Single(feed.Asked, asked => asked.StartsWith("x.pack ", StringComparison.Ordinal));
        Assert.Equal(files.First(file => file.Path == "PFiles64/AmneziaGeo/z.dll").Data, await File.ReadAllBytesAsync(Path.Combine(target, "PFiles64/AmneziaGeo/z.dll")));
        Assert.Equal(files.First(file => file.Path == Setup).Data, await File.ReadAllBytesAsync(Path.Combine(target, Setup)));
        Assert.False(Directory.Exists(target + ".old"));
    }

    [UnixFact("a package tree carries permissions and symbolic links")]
    [UnsupportedOSPlatform("windows")]
    public async Task FoldersLinksAndModes_ArePutInPlace()
    {
        var files = new List<(string Path, byte[] Data)>
        {
            ("DEBIAN/control", Encoding.UTF8.GetBytes("Package: amneziageo\n")),
            ("DEBIAN/postinst", Encoding.UTF8.GetBytes("#!/bin/sh\n")),
            ("usr/lib/amneziageo/amneziageo", Bytes(3000, 1)),
        };
        var (list, pack) = Pack(
            files,
            path => path.EndsWith("control", StringComparison.Ordinal) ? "644" : "755",
            "d 755 DEBIAN",
            "d 755 usr",
            "d 755 usr/bin",
            "d 755 usr/lib",
            "d 755 usr/lib/amneziageo",
            "d 755 var",
            "d 755 var/lib",
            "d 700 var/lib/amneziageo",
            "l ../lib/amneziageo/amneziageo usr/bin/amneziageo");
        var target = Path.Combine(_root, "tree");

        await new UpdatePieces(new HttpClient(new Feed(list, pack))).StageAsync(
            Release, Asset(list, pack, 100_000_000), [], target, null, CancellationToken.None);

        const UnixFileMode Executable = (UnixFileMode)0x1ed;
        Assert.Equal(Executable, File.GetUnixFileMode(Path.Combine(target, "usr/lib/amneziageo/amneziageo")));
        Assert.Equal((UnixFileMode)0x1a4, File.GetUnixFileMode(Path.Combine(target, "DEBIAN/control")));
        Assert.Equal((UnixFileMode)0x1c0, File.GetUnixFileMode(Path.Combine(target, "var/lib/amneziageo")));
        Assert.Equal("../lib/amneziageo/amneziageo", new FileInfo(Path.Combine(target, "usr/bin/amneziageo")).LinkTarget);
    }

    [Fact]
    public async Task AnEarlierVersionHeld_BringsADeltaInsteadOfTheFile()
    {
        var (earlier, current) = Versions();
        var files = Layout(heldSize: 2048);
        files[^1] = ("PFiles64/AmneziaGeo/z.dll", current);
        var (list, pack) = Pack(files);
        var (deltas, deltaPack) = Deltas((current, earlier));
        var feed = new Feed(list, pack) { Extra = { ["x.deltas"] = deltas, ["x.deltapack"] = deltaPack } };
        var held = Hold(files, "PFiles64/AmneziaGeo/a.dll", "PFiles64/AmneziaGeo/b.dll", "PFiles64/AmneziaGeo/runtimes/c.dll");
        File.WriteAllBytes(Path.Combine(held, "z.dll.was"), earlier);
        var target = Path.Combine(_root, "layout");

        var count = await new UpdatePieces(new HttpClient(feed)).StageAsync(
            Release, WithDeltas(Asset(list, pack, 100_000_000), deltas, deltaPack), [held], target, null, CancellationToken.None);

        foreach (var (path, data) in files)
        {
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(target, path)));
        }

        Assert.Equal(new UpdatePieceCount(6, 3, count.Bytes, 1), count);
        Assert.Equal(new[] { "x.files", "x.deltas", "x.pack", "x.deltapack" }, feed.Asked.Select(asked => asked.Split(' ')[0]));
        Assert.Contains($"x.deltapack 0-{deltaPack.Length - 1}", feed.Asked);
        Assert.DoesNotContain(feed.Asked, asked => asked.StartsWith("x.pack ", StringComparison.Ordinal) && asked.EndsWith($"-{pack.Length - 1}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeltaWhoseEarlierFileIsNotHeld_LeavesTheFileToThePack()
    {
        var (earlier, current) = Versions();
        var files = Layout(heldSize: 2048);
        files[^1] = ("PFiles64/AmneziaGeo/z.dll", current);
        var (list, pack) = Pack(files);
        var (deltas, deltaPack) = Deltas((current, earlier));
        var feed = new Feed(list, pack) { Extra = { ["x.deltas"] = deltas, ["x.deltapack"] = deltaPack } };
        var held = Hold(files, "PFiles64/AmneziaGeo/a.dll", "PFiles64/AmneziaGeo/b.dll", "PFiles64/AmneziaGeo/runtimes/c.dll");
        var target = Path.Combine(_root, "layout");

        var count = await new UpdatePieces(new HttpClient(feed)).StageAsync(
            Release, WithDeltas(Asset(list, pack, 100_000_000), deltas, deltaPack), [held], target, null, CancellationToken.None);

        Assert.Equal(current, await File.ReadAllBytesAsync(Path.Combine(target, "PFiles64/AmneziaGeo/z.dll")));
        Assert.Equal(new UpdatePieceCount(6, 3, count.Bytes), count);
        Assert.DoesNotContain(feed.Asked, asked => asked.StartsWith("x.deltapack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACorruptDelta_IsRefused()
    {
        var (earlier, current) = Versions();
        var files = Layout(heldSize: 2048);
        files[^1] = ("PFiles64/AmneziaGeo/z.dll", current);
        var (list, pack) = Pack(files);
        var (deltas, deltaPack) = Deltas((current, earlier));
        deltaPack[deltaPack.Length / 2] ^= 0xff;
        var feed = new Feed(list, pack) { Extra = { ["x.deltas"] = deltas, ["x.deltapack"] = deltaPack } };
        var held = Hold(files, "PFiles64/AmneziaGeo/a.dll");
        File.WriteAllBytes(Path.Combine(held, "z.dll.was"), earlier);

        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdatePieces(new HttpClient(feed)).StageAsync(
            Release, WithDeltas(Asset(list, pack, 100_000_000), deltas, deltaPack), [held], Path.Combine(_root, "layout"), null, CancellationToken.None));
    }

    [Fact]
    public async Task AListOfDeltasUnlikeTheManifestOne_IsRefused()
    {
        var (earlier, current) = Versions();
        var files = Layout(heldSize: 2048);
        var (list, pack) = Pack(files);
        var (deltas, deltaPack) = Deltas((current, earlier));
        var feed = new Feed(list, pack) { Extra = { ["x.deltas"] = deltas, ["x.deltapack"] = deltaPack } };
        var asset = WithDeltas(Asset(list, pack, 100_000_000), deltas, deltaPack);
        asset = asset with { Deltas = asset.Deltas! with { Sha256 = new string('0', 64) } };

        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdatePieces(new HttpClient(feed)).StageAsync(
            Release, asset, [], Path.Combine(_root, "layout"), null, CancellationToken.None));

        Assert.Equal(new[] { "x.files", "x.deltas" }, feed.Asked);
    }

    [Fact]
    public void TheManifest_NamesTheListsAndThePacksOfAnAsset()
    {
        const string Json = """
            {"version":"1.9.15.0","installers":[
              {"name":"AmneziaGeo-1.9.15.0-win-x64-fdd.exe","platform":"windows","arch":"x64","variant":"fdd","sha256":"ab","size":45000000,
               "files":{"name":"AmneziaGeo-1.9.15.0-win-x64-fdd.files","size":12000,"sha256":"cd"},
               "pack":{"name":"AmneziaGeo-1.9.15.0-win-x64-fdd.pack","size":47000000},
               "deltas":{"name":"AmneziaGeo-1.9.15.0-win-x64-fdd.deltas","size":900,"sha256":"gh"},
               "deltapack":{"name":"AmneziaGeo-1.9.15.0-win-x64-fdd.deltapack","size":4000000}},
              {"name":"AmneziaGeo-1.9.15.0-android.apk","platform":"android","arch":"universal","variant":"apk","sha256":"ef"}]}
            """;

        var manifest = UpdateFeed.ParseManifest(Json)!;
        var asset = UpdateFeed.AssetNamed(manifest, "AmneziaGeo-1.9.15.0-win-x64-fdd.exe")!;

        Assert.Equal(45000000, asset.Size);
        Assert.Equal(new UpdateFile("AmneziaGeo-1.9.15.0-win-x64-fdd.files", 12000, "cd"), asset.Files);
        Assert.Equal(new UpdateFile("AmneziaGeo-1.9.15.0-win-x64-fdd.pack", 47000000), asset.Pack);
        Assert.Equal(new UpdateFile("AmneziaGeo-1.9.15.0-win-x64-fdd.deltas", 900, "gh"), asset.Deltas);
        Assert.Equal(new UpdateFile("AmneziaGeo-1.9.15.0-win-x64-fdd.deltapack", 4000000), asset.DeltaPack);
        Assert.Null(UpdateFeed.AssetNamed(manifest, "AmneziaGeo-1.9.15.0-android.apk")!.Pack);
        Assert.Null(UpdateFeed.AssetNamed(manifest, "AmneziaGeo-1.9.15.0-android.apk")!.Deltas);
        Assert.Null(UpdateFeed.AssetNamed(manifest, "nothing.exe"));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Uri Release { get; } = new("https://example.test/releases/download/v9.9.9.9/AmneziaGeo-9.9.9.9-win-x64-fdd.exe");

    private static UpdateAsset Asset(byte[] list, byte[] pack, long size) => new(
        "AmneziaGeo-9.9.9.9-win-x64-fdd.exe",
        "windows",
        "x64",
        "fdd",
        new string('e', 64),
        size,
        new UpdateFile("x.files", list.Length, Sha(list)),
        new UpdateFile("x.pack", pack.Length));

    // The update layout of a Windows setup: the bundle and the MSI change, three runtime files stay, one of ours changes.
    private static List<(string Path, byte[] Data)> Layout(int heldSize) =>
    [
        (Msi, Bytes(1024, 1)),
        (Setup, Bytes(2048, 2)),
        ("PFiles64/AmneziaGeo/a.dll", Bytes(heldSize, 3)),
        ("PFiles64/AmneziaGeo/b.dll", Bytes(heldSize, 4)),
        ("PFiles64/AmneziaGeo/runtimes/c.dll", Bytes(heldSize, 5)),
        ("PFiles64/AmneziaGeo/z.dll", Bytes(1500, 6)),
    ];

    // Lays the files named out the way an installed copy holds them: flat, under names of their own.
    private string Hold(List<(string Path, byte[] Data)> files, params string[] names)
    {
        var held = Path.Combine(_root, "installed");
        Directory.CreateDirectory(held);
        foreach (var name in names)
        {
            File.WriteAllBytes(Path.Combine(held, Path.GetFileName(name) + ".held"), files.First(file => file.Path == name).Data);
        }

        return held;
    }

    private static (byte[] List, byte[] Pack) Pack(List<(string Path, byte[] Data)> files, params string[] extra) =>
        Pack(files, _ => "644", extra);

    private static (byte[] List, byte[] Pack) Pack(List<(string Path, byte[] Data)> files, Func<string, string> mode, params string[] extra)
    {
        using var pack = new MemoryStream();
        var lines = new List<string> { UpdateList.Head };
        foreach (var (path, data) in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var packed = Gzip(data);
            lines.Add($"{Sha(data)} {mode(path)} {data.Length} {pack.Length} {packed.Length} {path}");
            pack.Write(packed);
        }

        lines.AddRange(extra);
        return (Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"), pack.ToArray());
    }

    // Two versions of one of our files: the later one differs in a few bytes.
    private static (byte[] Earlier, byte[] Current) Versions()
    {
        var earlier = Bytes(64 * 1024, 7);
        var current = (byte[])earlier.Clone();
        current[100] ^= 1;
        current[40_000] ^= 2;
        return (earlier, current);
    }

    private static (byte[] List, byte[] Pack) Deltas(params (byte[] Current, byte[] Earlier)[] pairs)
    {
        using var pack = new MemoryStream();
        var lines = new List<string> { UpdateDeltaList.Head };
        foreach (var (current, earlier) in pairs)
        {
            var delta = UpdateDelta.Create(earlier, current);
            lines.Add($"{Sha(current)} {Sha(earlier)} {earlier.Length} {pack.Length} {delta.Length}");
            pack.Write(delta);
        }

        return (Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"), pack.ToArray());
    }

    private static UpdateAsset WithDeltas(UpdateAsset asset, byte[] list, byte[] pack) => asset with
    {
        Deltas = new UpdateFile("x.deltas", list.Length, Sha(list)),
        DeltaPack = new UpdateFile("x.deltapack", pack.Length),
    };

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Bytes(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    // A release server: the lists whole, the packs in the parts asked for.
    private sealed class Feed(byte[] list, byte[] pack) : HttpMessageHandler
    {
        private int _parts;

        public List<string> Asked { get; } = [];

        public bool Ranges { get; init; } = true;

        public int PartsBeforeDrop { get; init; } = int.MaxValue;

        public Dictionary<string, byte[]> Extra { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = request.RequestUri!.Segments[^1];
            var body = name == "x.files" ? list : Extra.TryGetValue(name, out var extra) ? extra : pack;
            var range = request.Headers.Range?.Ranges.First();
            Asked.Add(range is null ? name : $"{name} {range.From}-{range.To}");
            if (range is null || !Ranges)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
            }

            if (_parts++ >= PartsBeforeDrop)
            {
                throw new HttpRequestException("the connection dropped");
            }

            var from = range.From ?? 0;
            var to = range.To ?? body.Length - 1;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(body[(int)from..(int)(to + 1)]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, body.Length);
            return Task.FromResult(response);
        }
    }
}
