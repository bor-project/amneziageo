using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AmneziaGeo.Decl;
using AmneziaGeo.Deltas;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The release tool makes deltas of the changed files of an asset from their versions in earlier releases, found in a
/// folder or downloaded in parts, and the update puts the asset together from them and the files the machine holds.
/// </summary>
public sealed class DeltaBuilderTests : IDisposable
{
    private const string Earlier = "1.0.0.1";
    private const string Current = "1.0.0.2";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ag-deltas-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ChangedFiles_GetDeltasFromAnEarlierReleaseInAFolder()
    {
        var (earlier, current) = Releases();
        var from = Publish(Earlier, earlier);
        var output = Folder("out");

        var summary = await Builder().BuildAsync(List(Publish(Current, current)), Current, [new DeltaBase(Earlier, from)], output, CancellationToken.None);

        Assert.Equal(2, summary!.Deltas);
        Assert.Equal(1, summary.Bases);
        AssertDeltas(output, earlier, current, "b.dll", "z.dll");
    }

    [Fact]
    public async Task ChangedFiles_GetDeltasFromAnEarlierReleaseDownloadedInParts()
    {
        var (earlier, current) = Releases();
        var from = Publish(Earlier, earlier);
        var server = new Server(Directory.GetFiles(from).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes));
        var output = Folder("out");

        var summary = await new DeltaBuilder(new HttpClient(server), TimeSpan.FromMinutes(1), new StringWriter())
            .BuildAsync(List(Publish(Current, current)), Current, [new DeltaBase(Earlier, "https://example.test/download/v1.0.0.1/")], output, CancellationToken.None);

        Assert.Equal(2, summary!.Deltas);
        AssertDeltas(output, earlier, current, "b.dll", "z.dll");
        Assert.Equal("AmneziaGeo-1.0.0.1-win-x64-fdd.files", server.Asked[0]);
        Assert.Equal("AmneziaGeo-1.0.0.1-win-x64-fdd.pack 0-0", server.Asked[1]);
        Assert.Equal(4, server.Asked.Count);
    }

    [Fact]
    public async Task NoEarlierList_GivesNoDeltas()
    {
        var (_, current) = Releases();
        var output = Folder("out");

        var summary = await Builder().BuildAsync(List(Publish(Current, current)), Current, [new DeltaBase(Earlier, Folder("none"))], output, CancellationToken.None);

        Assert.Null(summary);
        Assert.Empty(Directory.GetFiles(output));
    }

    [Fact]
    public async Task TheUpdate_PutsTheAssetTogetherFromTheDeltas()
    {
        var (earlier, current) = Releases();
        var from = Publish(Earlier, earlier);
        var published = Publish(Current, current);
        var output = Folder("out");
        await Builder().BuildAsync(List(published), Current, [new DeltaBase(Earlier, from)], output, CancellationToken.None);
        var files = Directory.GetFiles(published).Concat(Directory.GetFiles(output)).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
        var held = Folder("installed");
        foreach (var (name, data) in earlier)
        {
            File.WriteAllBytes(Path.Combine(held, name), data);
        }

        const string Stem = "AmneziaGeo-1.0.0.2-win-x64-fdd";
        var asset = new UpdateAsset(
            Stem + ".exe",
            "windows",
            "x64",
            "fdd",
            new string('e', 64),
            100_000_000,
            new UpdateFile(Stem + ".files", files[Stem + ".files"].Length, Sha(files[Stem + ".files"])),
            new UpdateFile(Stem + ".pack", files[Stem + ".pack"].Length),
            new UpdateFile(Stem + ".deltas", files[Stem + ".deltas"].Length, Sha(files[Stem + ".deltas"])),
            new UpdateFile(Stem + ".deltapack", files[Stem + ".deltapack"].Length));
        var target = Folder("layout");

        var count = await new UpdatePieces(new HttpClient(new Server(files))).StageAsync(
            new Uri("https://example.test/download/v1.0.0.2/" + Stem + ".exe"), asset, [held], target, null, CancellationToken.None);

        foreach (var (name, data) in current)
        {
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(target, "PFiles64", "AmneziaGeo", name)));
        }

        Assert.Equal(new UpdatePieceCount(5, 4, count.Bytes, 2), count);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static DeltaBuilder Builder() => new(new HttpClient(), TimeSpan.FromMinutes(1), new StringWriter());

    // Two releases of an asset: a.dll stays, b.dll and z.dll change a little, r.bin changes and does not compress, n.dll
    // is new.
    private static (Dictionary<string, byte[]> Earlier, Dictionary<string, byte[]> Current) Releases()
    {
        var earlier = new Dictionary<string, byte[]>
        {
            ["a.dll"] = Words(40_000, 1),
            ["b.dll"] = Words(60_000, 2),
            ["r.bin"] = Noise(20_000, 3),
            ["z.dll"] = Words(80_000, 4),
        };
        var b = (byte[])earlier["b.dll"].Clone();
        b[100] ^= 1;
        var z = earlier["z.dll"].Take(50_000).Concat(Words(300, 5)).Concat(earlier["z.dll"].Skip(50_000)).ToArray();
        var current = new Dictionary<string, byte[]>
        {
            ["a.dll"] = earlier["a.dll"],
            ["b.dll"] = b,
            ["n.dll"] = Words(10_000, 6),
            ["r.bin"] = Noise(20_000, 7),
            ["z.dll"] = z,
        };
        return (earlier, current);
    }

    private static void AssertDeltas(string output, Dictionary<string, byte[]> earlier, Dictionary<string, byte[]> current, params string[] names)
    {
        var stem = Path.Combine(output, "AmneziaGeo-1.0.0.2-win-x64-fdd");
        var pack = File.ReadAllBytes(stem + ".deltapack");
        var entries = UpdateDeltaList.Parse(File.ReadAllBytes(stem + ".deltas"), pack.Length);
        Assert.Equal(names.Length, entries.Count);
        foreach (var name in names)
        {
            var entry = Assert.Single(entries, entry => entry.Sha256 == Sha(current[name]));
            Assert.Equal(Sha(earlier[name]), entry.Base);
            Assert.Equal(earlier[name].Length, entry.BaseSize);
            using var made = new MemoryStream();
            UpdateDelta.Apply(earlier[name], pack[(int)entry.Offset..(int)(entry.Offset + entry.Length)], made, current[name].Length);
            Assert.Equal(current[name], made.ToArray());
        }
    }

    // Writes the list of files and the pack of an asset of the version given the way a release carries them.
    private string Publish(string version, Dictionary<string, byte[]> files)
    {
        var folder = Folder(version);
        using var pack = new MemoryStream();
        var lines = new List<string> { UpdateList.Head };
        foreach (var (name, data) in files.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            var packed = Gzip(data);
            lines.Add($"{Sha(data)} 644 {data.Length} {pack.Length} {packed.Length} PFiles64/AmneziaGeo/{name}");
            pack.Write(packed);
        }

        var stem = Path.Combine(folder, $"AmneziaGeo-{version}-win-x64-fdd");
        File.WriteAllText(stem + ".files", string.Join('\n', lines) + "\n");
        File.WriteAllBytes(stem + ".pack", pack.ToArray());
        return folder;
    }

    private static string List(string folder) => Directory.GetFiles(folder, "*.files").Single();

    private string Folder(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
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

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    // A release server: every file whole or in the part asked for.
    private sealed class Server(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = request.RequestUri!.Segments[^1];
            var range = request.Headers.Range?.Ranges.First();
            lock (Asked)
            {
                Asked.Add(range is null ? name : $"{name} {range.From}-{range.To}");
            }

            if (!files.TryGetValue(name, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (range is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
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
