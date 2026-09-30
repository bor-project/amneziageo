using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace AmneziaGeo.Decl;

/// <summary>
/// What putting an asset together from pieces took.
/// </summary>
/// <param name="Files">How many files the asset holds.</param>
/// <param name="Fetched">How many of them were downloaded, whole from the pack or as deltas.</param>
/// <param name="Bytes">How many bytes of the packs were downloaded.</param>
/// <param name="Deltas">How many of the files downloaded were made from a delta and an earlier file the machine holds.</param>
public sealed record UpdatePieceCount(int Files, int Fetched, long Bytes, int Deltas = 0);

/// <summary>
/// Puts a release asset together from the files the machine holds and the files it lacks, fetched from the pack of
/// the release in parts or made from the deltas of the release and the earlier versions the machine holds.
/// </summary>
public sealed class UpdatePieces
{
    /// <summary>
    /// The share of the asset past which the whole asset is downloaded instead.
    /// </summary>
    public const double MostShare = 0.75;

    /// <summary>
    /// The most parts of the packs an update asks for.
    /// </summary>
    public const int MostParts = 64;

    /// <summary>
    /// The largest stretch of a pack between two pieces the machine lacks that is downloaded along with them.
    /// </summary>
    public const long Gap = 256 * 1024;

    private const UnixFileMode Folder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;

    /// <summary>
    /// ctor
    /// </summary>
    public UpdatePieces(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Puts the asset into a folder from the files found under the folders held, the pack beside the address of the
    /// asset and the deltas beside it, checking every file against the list of the asset, which is kept beside the
    /// folder. What an earlier attempt left in the folder is taken up. Progress hears the bytes fetched and the bytes
    /// to fetch.
    /// </summary>
    public async Task<UpdatePieceCount> StageAsync(
        Uri address,
        UpdateAsset asset,
        IReadOnlyList<string> held,
        string target,
        Action<long, long>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(held);

        if (asset.Files is not { Name: { Length: > 0 } listName, Sha256: { Length: > 0 } listDigest } list)
        {
            throw new InvalidDataException("the release lists no files of its asset");
        }

        if (asset.Pack is not { Name: { Length: > 0 } packName, Size: > 0 } pack)
        {
            throw new InvalidDataException("the release carries no pack");
        }

        var text = await ReadListAsync(new Uri(address, listName), listName, list.Size, listDigest, ct).ConfigureAwait(false);
        var entries = UpdateList.Parse(text, pack.Size);
        var (deltas, deltaPack) = await ReadDeltasAsync(address, asset, ct).ConfigureAwait(false);

        var earlier = target + ".old";
        Remove(earlier);
        if (Directory.Exists(target))
        {
            Directory.Move(target, earlier);
        }

        try
        {
            Directory.CreateDirectory(target);
            await File.WriteAllBytesAsync(target + ".files", text, ct).ConfigureAwait(false);

            var files = entries.Where(entry => entry.Kind == UpdateEntryKind.File).ToList();
            var found = Holdings([.. held, earlier], files, deltas);
            var lacking = files.Where(entry => !found.ContainsKey(entry.Sha256)).ToList();
            var patches = Patches(lacking, deltas, found);
            var made = lacking
                .Where(entry => patches.ContainsKey(entry.Sha256))
                .GroupBy(entry => entry.Sha256, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var parts = Parts(lacking
                .Where(entry => !patches.ContainsKey(entry.Sha256))
                .Select(entry => new Piece(entry.Offset, entry.Length, entry, null)));
            var patchParts = Parts(made.Values
                .Select(entry => new Piece(patches[entry.Sha256].Offset, patches[entry.Sha256].Length, entry, patches[entry.Sha256])));
            var bytes = parts.Sum(part => part.Length) + patchParts.Sum(part => part.Length);
            if (bytes > (asset.Size > 0 ? asset.Size : pack.Size) * MostShare)
            {
                throw new InvalidDataException("most of the asset changed");
            }

            if (parts.Count + patchParts.Count > MostParts)
            {
                throw new InvalidDataException($"the changed files lie in {parts.Count + patchParts.Count} parts of the packs");
            }

            foreach (var entry in files)
            {
                if (found.TryGetValue(entry.Sha256, out var source))
                {
                    await CopyAsync(source, entry, Place(target, entry.Path), ct).ConfigureAwait(false);
                }
            }

            var packed = new Uri(address, packName);
            var done = 0L;
            progress?.Invoke(done, bytes);
            foreach (var part in parts)
            {
                await FetchAsync(packed, part, target, found, ct).ConfigureAwait(false);
                done += part.Length;
                progress?.Invoke(done, bytes);
            }

            if (deltaPack is not null)
            {
                foreach (var part in patchParts)
                {
                    await FetchAsync(deltaPack, part, target, found, ct).ConfigureAwait(false);
                    done += part.Length;
                    progress?.Invoke(done, bytes);
                }
            }

            foreach (var entry in lacking)
            {
                if (made.TryGetValue(entry.Sha256, out var first) && first.Path != entry.Path)
                {
                    await CopyAsync(Place(target, first.Path), entry, Place(target, entry.Path), ct).ConfigureAwait(false);
                }
            }

            foreach (var entry in entries.Where(entry => entry.Kind == UpdateEntryKind.Folder))
            {
                var place = Place(target, entry.Path);
                MakeFolder(place);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(place, entry.Mode);
                }
            }

            foreach (var entry in entries.Where(entry => entry.Kind == UpdateEntryKind.Link))
            {
                if (OperatingSystem.IsWindows())
                {
                    throw new InvalidDataException($"the list of files names the link {entry.Path}");
                }

                var place = Place(target, entry.Path);
                MakeFolder(Path.GetDirectoryName(place)!);
                File.CreateSymbolicLink(place, entry.Target);
            }

            return new UpdatePieceCount(files.Count, lacking.Count, bytes, lacking.Count(entry => patches.ContainsKey(entry.Sha256)));
        }
        finally
        {
            Remove(earlier);
        }
    }

    private static Dictionary<string, string> Holdings(
        IReadOnlyList<string> held,
        IReadOnlyList<UpdateEntry> files,
        IReadOnlyList<UpdateDeltaEntry> deltas)
    {
        var sizes = files.Select(entry => entry.Size).Concat(deltas.Select(delta => delta.BaseSize)).ToHashSet();
        var wanted = files.Select(entry => entry.Sha256).Concat(deltas.Select(delta => delta.Base)).ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var folder in held.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*", options))
            {
                if (!sizes.Contains(new FileInfo(path).Length))
                {
                    continue;
                }

                var sha = Digest(path);
                if (wanted.Contains(sha))
                {
                    found.TryAdd(sha, path);
                }
            }
        }

        return found;
    }

    // The smallest delta for every lacking file whose earlier version the machine holds, where it is smaller than the
    // file packed whole.
    private static Dictionary<string, UpdateDeltaEntry> Patches(
        IReadOnlyList<UpdateEntry> lacking,
        IReadOnlyList<UpdateDeltaEntry> deltas,
        Dictionary<string, string> found)
    {
        var byTarget = deltas.Where(delta => found.ContainsKey(delta.Base)).ToLookup(delta => delta.Sha256, StringComparer.Ordinal);
        var patches = new Dictionary<string, UpdateDeltaEntry>(StringComparer.Ordinal);
        foreach (var entry in lacking)
        {
            if (byTarget[entry.Sha256].Where(delta => delta.Length < entry.Length).MinBy(delta => delta.Length) is { } best)
            {
                patches.TryAdd(entry.Sha256, best);
            }
        }

        return patches;
    }

    private static string Digest(string path)
    {
        using var file = File.OpenRead(path);

        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    private static List<Part> Parts(IEnumerable<Piece> pieces)
    {
        var parts = new List<Part>();
        foreach (var piece in pieces.OrderBy(piece => piece.Offset))
        {
            if (parts.Count > 0 && piece.Offset - parts[^1].End <= Gap)
            {
                parts[^1].Add(piece);
            }
            else
            {
                parts.Add(new Part(piece));
            }
        }

        return parts;
    }

    private async Task<(IReadOnlyList<UpdateDeltaEntry> Entries, Uri? Pack)> ReadDeltasAsync(Uri address, UpdateAsset asset, CancellationToken ct)
    {
        if (asset.Deltas is not { Name: { Length: > 0 } listName, Sha256: { Length: > 0 } listDigest } list
            || asset.DeltaPack is not { Name: { Length: > 0 } packName, Size: > 0 } pack)
        {
            return ([], null);
        }

        var text = await ReadListAsync(new Uri(address, listName), listName, list.Size, listDigest, ct).ConfigureAwait(false);
        return (UpdateDeltaList.Parse(text, pack.Size), new Uri(address, packName));
    }

    private async Task<byte[]> ReadListAsync(Uri address, string name, long size, string digest, CancellationToken ct)
    {
        if (size is <= 0 or > UpdateList.MaxSize)
        {
            throw new InvalidDataException($"{name} is not a size a list takes");
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(StallLimit);
        using var response = await _http
            .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, limit.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{address} answered {(int)response.StatusCode}");
        }

        if (response.Content.Headers.ContentLength is { } length && length != size)
        {
            throw new InvalidDataException($"{name} is not the size the manifest names");
        }

        using var source = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
        var text = new byte[size];
        await FillAsync(source, text, limit).ConfigureAwait(false);
        if (await source.ReadAsync(new byte[1], limit.Token).ConfigureAwait(false) > 0)
        {
            throw new InvalidDataException($"{name} is larger than the manifest says");
        }

        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(text)), digest, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{name} does not match the digest the manifest names");
        }

        return text;
    }

    private async Task FetchAsync(Uri address, Part part, string target, Dictionary<string, string> found, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(StallLimit);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Range = new RangeHeaderValue(part.Start, part.End - 1);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new InvalidDataException($"{address} answered {(int)response.StatusCode} to a request for a part");
        }

        var range = response.Content.Headers.ContentRange;
        if (range is null || range.From != part.Start || range.To != part.End - 1)
        {
            throw new InvalidDataException($"{address} answered with another part than the one asked for");
        }

        using var source = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
        var at = part.Start;
        foreach (var piece in part.Pieces)
        {
            await SkipAsync(source, piece.Offset - at, limit).ConfigureAwait(false);
            var packed = new byte[piece.Length];
            await FillAsync(source, packed, limit).ConfigureAwait(false);
            at = piece.Offset + piece.Length;
            var place = Place(target, piece.Entry.Path);
            if (piece.Delta is { } delta)
            {
                await PatchAsync(packed, piece.Entry, found[delta.Base], place, ct).ConfigureAwait(false);
            }
            else
            {
                await UnpackAsync(packed, piece.Entry, place, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task SkipAsync(Stream source, long count, CancellationTokenSource limit)
    {
        var chunk = new byte[81920];
        var left = count;
        while (left > 0)
        {
            var read = await source.ReadAsync(chunk.AsMemory(0, (int)Math.Min(left, chunk.Length)), limit.Token)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("the part of the pack ended early");
            }

            limit.CancelAfter(StallLimit);
            left -= read;
        }
    }

    private static async Task FillAsync(Stream source, byte[] buffer, CancellationTokenSource limit)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(done), limit.Token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("the download ended early");
            }

            limit.CancelAfter(StallLimit);
            done += read;
        }
    }

    private static async Task UnpackAsync(byte[] packed, UpdateEntry entry, string path, CancellationToken ct)
    {
        MakeFolder(Path.GetDirectoryName(path)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var gzip = new GZipStream(new MemoryStream(packed, writable: false), CompressionMode.Decompress))
        using (var file = File.Create(path))
        {
            var chunk = new byte[81920];
            var total = 0L;
            var read = 0;
            while ((read = await gzip.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > entry.Size)
                {
                    throw new InvalidDataException($"{entry.Path} unpacks larger than the list says");
                }

                hash.AppendData(chunk, 0, read);
                await file.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }

        Seal(path, entry, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static async Task PatchAsync(byte[] delta, UpdateEntry entry, string source, string path, CancellationToken ct)
    {
        MakeFolder(Path.GetDirectoryName(path)!);
        var earlier = await File.ReadAllBytesAsync(source, ct).ConfigureAwait(false);
        using (var file = File.Create(path))
        {
            UpdateDelta.Apply(earlier, delta, file, entry.Size);
        }

        Seal(path, entry, Digest(path));
    }

    private static async Task CopyAsync(string source, UpdateEntry entry, string path, CancellationToken ct)
    {
        MakeFolder(Path.GetDirectoryName(path)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var from = File.OpenRead(source))
        using (var file = File.Create(path))
        {
            var chunk = new byte[81920];
            var read = 0;
            while ((read = await from.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(chunk, 0, read);
                await file.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }

        Seal(path, entry, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void Seal(string path, UpdateEntry entry, string digest)
    {
        if (!string.Equals(digest, entry.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{entry.Path} does not match the digest the list names");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, entry.Mode);
        }
    }

    private static void MakeFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        if (Directory.Exists(path))
        {
            return;
        }

        if (Path.GetDirectoryName(path) is { Length: > 0 } parent)
        {
            MakeFolder(parent);
        }

        Directory.CreateDirectory(path, Folder);
    }

    private static string Place(string target, string path)
    {
        var root = Path.GetFullPath(target);
        var place = Path.GetFullPath(Path.Combine(root, path));
        if (!place.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{path} lies outside the asset");
        }

        return place;
    }

    private static void Remove(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // A file the machine lacks: where its bytes lie in a pack, and the delta they are when they are one.
    private sealed record Piece(long Offset, long Length, UpdateEntry Entry, UpdateDeltaEntry? Delta);

    private sealed class Part
    {
        private readonly List<Piece> _pieces = [];

        /// <summary>
        /// ctor
        /// </summary>
        public Part(Piece first)
        {
            Start = first.Offset;
            Add(first);
        }

        public long Start { get; }

        public long End { get; private set; }

        public long Length => End - Start;

        public IReadOnlyList<Piece> Pieces => _pieces;

        public void Add(Piece piece)
        {
            _pieces.Add(piece);
            End = piece.Offset + piece.Length;
        }
    }
}
