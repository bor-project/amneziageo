using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Deltas;

/// <summary>
/// An earlier release the deltas start from: the version its files are named with and where they lie, a folder or the
/// address its files are downloaded from.
/// </summary>
/// <param name="Version">The version in the names of its files.</param>
/// <param name="Source">A folder, or an address ending with a slash.</param>
public sealed record DeltaBase(string Version, string Source);

/// <summary>
/// What the deltas of one asset came to.
/// </summary>
/// <param name="Name">The list of files of the asset, without its extension.</param>
/// <param name="Deltas">How many deltas were made.</param>
/// <param name="Bases">How many earlier releases carried the list of the asset.</param>
/// <param name="Bytes">The size of the pack of deltas.</param>
/// <param name="Whole">What the files the deltas make take in the pack of the asset.</param>
public sealed record DeltaSummary(string Name, int Deltas, int Bases, long Bytes, long Whole);

/// <summary>
/// Makes the deltas of the changed files of a release asset from their versions in earlier releases and writes the
/// list of deltas and the pack of deltas beside the list of files of the asset.
/// </summary>
public sealed class DeltaBuilder
{
    /// <summary>
    /// The share of the file packed whole a delta stays under to be kept.
    /// </summary>
    public const double MostShare = 0.75;

    /// <summary>
    /// The share of a file its packing saves at least for the file to get a delta; a file that does not compress
    /// holds packed data a delta does not shrink.
    /// </summary>
    public const double LeastSaving = 0.1;

    private readonly HttpClient _http;
    private readonly TimeSpan _limit;
    private readonly TextWriter _log;

    /// <summary>
    /// ctor
    /// </summary>
    public DeltaBuilder(HttpClient http, TimeSpan limit, TextWriter log)
    {
        _http = http;
        _limit = limit;
        _log = TextWriter.Synchronized(log);
    }

    /// <summary>
    /// Makes the deltas of the asset whose list of files is given, from the releases given, into the folder given;
    /// null when no earlier release gives a delta.
    /// </summary>
    public async Task<DeltaSummary?> BuildAsync(string list, string version, IReadOnlyList<DeltaBase> bases, string output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(bases);

        var name = Path.GetFileName(list);
        if (!name.EndsWith(".files", StringComparison.Ordinal) || !name.Contains(version, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{name} is not a list of files of version {version}");
        }

        var stem = name[..^".files".Length];
        var pack = Path.ChangeExtension(list, ".pack");
        var entries = UpdateList.Parse(await File.ReadAllBytesAsync(list, ct).ConfigureAwait(false), new FileInfo(pack).Length)
            .Where(entry => entry.Kind == UpdateEntryKind.File)
            .ToList();

        var jobs = new List<Job>();
        var pairs = new HashSet<(string, string)>();
        var carried = 0;
        foreach (var source in bases)
        {
            var earlierStem = stem.Replace(version, source.Version, StringComparison.Ordinal);
            var earlier = await ReadListAsync(source, earlierStem, ct).ConfigureAwait(false);
            if (earlier is null)
            {
                continue;
            }

            carried++;
            foreach (var entry in entries)
            {
                if (earlier.TryGetValue(entry.Path, out var was)
                    && was.Sha256 != entry.Sha256
                    && was.Size <= UpdateDelta.MaxSize
                    && entry.Size <= UpdateDelta.MaxSize
                    && entry.Length <= entry.Size * (1 - LeastSaving)
                    && pairs.Add((entry.Sha256, was.Sha256)))
                {
                    jobs.Add(new Job(entry, was, source, earlierStem + ".pack"));
                }
            }
        }

        var made = new ConcurrentBag<(Job Job, byte[] Delta)>();
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };
        await Parallel.ForEachAsync(jobs, options, async (job, token) =>
        {
            if (await DeltaAsync(job, pack, token).ConfigureAwait(false) is { } delta)
            {
                made.Add((job, delta));
            }
        }).ConfigureAwait(false);

        if (made.IsEmpty)
        {
            _log.WriteLine($"{stem}: no delta from {carried} earlier releases that carry its list");
            return null;
        }

        var text = new StringBuilder(UpdateDeltaList.Head).Append('\n');
        var offset = 0L;
        using (var file = File.Create(Path.Combine(output, stem + ".deltapack")))
        {
            foreach (var (job, delta) in made.OrderBy(item => item.Job.Current.Path, StringComparer.Ordinal).ThenBy(item => item.Job.Earlier.Sha256, StringComparer.Ordinal))
            {
                text.Append(CultureInfo.InvariantCulture, $"{job.Current.Sha256} {job.Earlier.Sha256} {job.Earlier.Size} {offset} {delta.Length}\n");
                await file.WriteAsync(delta, ct).ConfigureAwait(false);
                offset += delta.Length;
            }
        }

        await File.WriteAllTextAsync(Path.Combine(output, stem + ".deltas"), text.ToString(), ct).ConfigureAwait(false);
        var summary = new DeltaSummary(stem, made.Count, carried, offset, made.Sum(item => item.Job.Current.Length));
        _log.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{stem}: {summary.Deltas} deltas from {summary.Bases} earlier releases, {summary.Bytes / 1e6:F1} MB for files that take {summary.Whole / 1e6:F1} MB in the pack"));
        return summary;
    }

    private async Task<byte[]?> DeltaAsync(Job job, string pack, CancellationToken ct)
    {
        try
        {
            var earlier = Unpack(await ReadPieceAsync(job.Source, job.Pack, job.Earlier, ct).ConfigureAwait(false), job.Earlier);
            var current = Unpack(await ReadLocalPieceAsync(pack, job.Current, ct).ConfigureAwait(false), job.Current);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(_limit);
            var delta = UpdateDelta.Create(earlier, current, limit.Token);
            return delta.Length < job.Current.Length * MostShare ? delta : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{job.Current.Path}: no delta from {job.Source.Version} within {_limit.TotalSeconds:F0} s"));
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException)
        {
            _log.WriteLine($"{job.Current.Path}: no delta from {job.Source.Version}: {ex.Message}");
            return null;
        }
    }

    // The files of the list of an earlier release by path; null when the release carries no list for the asset.
    private async Task<Dictionary<string, UpdateEntry>?> ReadListAsync(DeltaBase source, string stem, CancellationToken ct)
    {
        try
        {
            var text = await ReadAsync(source, stem + ".files", ct).ConfigureAwait(false);
            if (text is null)
            {
                return null;
            }

            var size = await SizeAsync(source, stem + ".pack", ct).ConfigureAwait(false);
            return UpdateList.Parse(text, size)
                .Where(entry => entry.Kind == UpdateEntryKind.File)
                .ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException)
        {
            _log.WriteLine($"{stem}: the earlier release does not give its list: {ex.Message}");
            return null;
        }
    }

    private async Task<byte[]?> ReadAsync(DeltaBase source, string name, CancellationToken ct)
    {
        if (!IsAddress(source))
        {
            var path = Path.Combine(source.Source, name);
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;
        }

        using var response = await _http.GetAsync(new Uri(new Uri(source.Source), name), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task<long> SizeAsync(DeltaBase source, string name, CancellationToken ct)
    {
        if (!IsAddress(source))
        {
            return new FileInfo(Path.Combine(source.Source, name)).Length;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(source.Source), name));
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.Length is not { } length)
        {
            throw new InvalidDataException($"{name} answered {(int)response.StatusCode} to a request for a part");
        }

        return length;
    }

    private async Task<byte[]> ReadPieceAsync(DeltaBase source, string name, UpdateEntry entry, CancellationToken ct)
    {
        if (!IsAddress(source))
        {
            return await ReadLocalPieceAsync(Path.Combine(source.Source, name), entry, ct).ConfigureAwait(false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(source.Source), name));
        request.Headers.Range = new RangeHeaderValue(entry.Offset, entry.Offset + entry.Length - 1);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != entry.Offset || range.To != entry.Offset + entry.Length - 1)
        {
            throw new InvalidDataException($"{name} answered {(int)response.StatusCode} with another part than the one asked for");
        }

        var packed = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return packed.Length == entry.Length ? packed : throw new InvalidDataException($"{name} gave a part of another size");
    }

    private static async Task<byte[]> ReadLocalPieceAsync(string path, UpdateEntry entry, CancellationToken ct)
    {
        using var file = File.OpenHandle(path);
        var packed = new byte[entry.Length];
        var read = await RandomAccess.ReadAsync(file, packed, entry.Offset, ct).ConfigureAwait(false);
        return read == packed.Length ? packed : throw new InvalidDataException($"{path} ends before {entry.Path}");
    }

    private static byte[] Unpack(byte[] packed, UpdateEntry entry)
    {
        using var gzip = new GZipStream(new MemoryStream(packed, writable: false), CompressionMode.Decompress);
        var data = new byte[entry.Size];
        gzip.ReadExactly(data);
        if (gzip.ReadByte() >= 0 || Convert.ToHexStringLower(SHA256.HashData(data)) != entry.Sha256)
        {
            throw new InvalidDataException($"{entry.Path} does not match the digest its list names");
        }

        return data;
    }

    private static bool IsAddress(DeltaBase source) =>
        source.Source.StartsWith("https://", StringComparison.Ordinal) || source.Source.StartsWith("http://", StringComparison.Ordinal);

    // A file to make a delta for: its current and earlier entries and where the earlier one lies.
    private sealed record Job(UpdateEntry Current, UpdateEntry Earlier, DeltaBase Source, string Pack);
}
