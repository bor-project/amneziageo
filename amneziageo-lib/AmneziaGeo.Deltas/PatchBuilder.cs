using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AmneziaGeo.Decl;

namespace AmneziaGeo.Deltas;

/// <summary>
/// What a patch came to.
/// </summary>
/// <param name="Files">How many files the tree holds.</param>
/// <param name="Held">How many of them a machine with the earlier tree holds.</param>
/// <param name="Deltas">How many are made from a delta.</param>
/// <param name="Whole">How many are carried whole.</param>
/// <param name="Bytes">The size of the patch.</param>
public sealed record PatchSummary(int Files, int Held, int Deltas, int Whole, long Bytes);

/// <summary>
/// Makes the patch that turns the files a machine holds of an earlier tree into a new tree.
/// </summary>
public sealed class PatchBuilder
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

    private const UnixFileMode Plain = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static readonly DateTimeOffset _stamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TimeSpan _limit;
    private readonly TextWriter _log;

    /// <summary>
    /// ctor
    /// </summary>
    public PatchBuilder(TimeSpan limit, TextWriter log)
    {
        _limit = limit;
        _log = TextWriter.Synchronized(log);
    }

    /// <summary>
    /// Writes the patch of the tree: a file found among the held files of the earlier tree is left to the machine, a
    /// changed one goes as a delta from the held file at its path where that is smaller, anything else whole. The
    /// held files of the earlier tree are those under the folders named, all of them when none is named.
    /// </summary>
    public async Task<PatchSummary> BuildAsync(string tree, string earlier, IReadOnlyList<string> held, string output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(held);

        var entries = Walk(tree);
        var before = Before(earlier, held);
        var known = before.Values.Select(file => file.Sha256).ToHashSet(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var jobs = new List<Job>();
        foreach (var (entry, path) in entries.Where(item => item.Entry.Sha256.Length > 0))
        {
            if (known.Contains(entry.Sha256) || !sources.TryAdd(entry.Sha256, path))
            {
                continue;
            }

            if (before.TryGetValue(entry.Path, out var was) && was.Size <= UpdateDelta.MaxSize && entry.Size <= UpdateDelta.MaxSize)
            {
                jobs.Add(new Job(entry, path, was));
            }
        }

        var deltas = new ConcurrentDictionary<string, (Earlier Was, byte[] Delta)>(StringComparer.Ordinal);
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };
        await Parallel.ForEachAsync(jobs, options, async (job, token) =>
        {
            if (await DeltaAsync(job, token).ConfigureAwait(false) is { } delta)
            {
                deltas[job.Entry.Sha256] = (job.Was, delta);
            }
        }).ConfigureAwait(false);

        var listed = entries.Select(item => Listed(item.Entry, known, deltas)).ToList();
        var text = new StringBuilder(UpdatePatch.Head).Append('\n');
        foreach (var entry in listed)
        {
            text.Append(UpdatePatch.Line(entry)).Append('\n');
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using (var file = File.Create(output))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            await AddAsync(archive, UpdatePatch.ListName, new MemoryStream(Encoding.UTF8.GetBytes(text.ToString())), CompressionLevel.SmallestSize, ct)
                .ConfigureAwait(false);
            foreach (var sha in sources.Keys.Where(sha => !deltas.ContainsKey(sha)).Order(StringComparer.Ordinal))
            {
                using var source = File.OpenRead(sources[sha]);
                await AddAsync(archive, UpdatePatch.WholeFolder + sha, source, CompressionLevel.SmallestSize, ct).ConfigureAwait(false);
            }

            foreach (var sha in deltas.Keys.Order(StringComparer.Ordinal))
            {
                await AddAsync(archive, UpdatePatch.DeltaFolder + sha, new MemoryStream(deltas[sha].Delta), CompressionLevel.NoCompression, ct)
                    .ConfigureAwait(false);
            }
        }

        var summary = new PatchSummary(
            listed.Count(entry => entry.Sha256.Length > 0),
            listed.Count(entry => entry.Kind == UpdatePatchKind.Held),
            listed.Count(entry => entry.Kind == UpdatePatchKind.Delta),
            listed.Count(entry => entry.Kind == UpdatePatchKind.Whole),
            new FileInfo(output).Length);
        _log.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{Path.GetFileName(output)}: {summary.Files} files, {summary.Held} held by the machine, {summary.Deltas} as deltas, {summary.Whole} whole, {summary.Bytes / 1e6:F2} MB"));
        return summary;
    }

    // The entry as the list names it: held, a delta or whole.
    private static UpdatePatchEntry Listed(
        UpdatePatchEntry entry,
        HashSet<string> known,
        ConcurrentDictionary<string, (Earlier Was, byte[] Delta)> deltas)
    {
        if (entry.Sha256.Length == 0)
        {
            return entry;
        }

        if (known.Contains(entry.Sha256))
        {
            return entry with { Kind = UpdatePatchKind.Held };
        }

        return deltas.TryGetValue(entry.Sha256, out var made)
            ? entry with { Kind = UpdatePatchKind.Delta, Base = made.Was.Sha256, BaseSize = made.Was.Size }
            : entry;
    }

    private async Task<byte[]?> DeltaAsync(Job job, CancellationToken ct)
    {
        var current = await File.ReadAllBytesAsync(job.Path, ct).ConfigureAwait(false);
        var packed = Packed(current);
        if (packed > current.Length * (1 - LeastSaving))
        {
            return null;
        }

        try
        {
            var earlier = await File.ReadAllBytesAsync(job.Was.Path, ct).ConfigureAwait(false);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(_limit);
            var delta = UpdateDelta.Create(earlier, current, limit.Token);
            return delta.Length < packed * MostShare ? delta : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{job.Entry.Path}: no delta within {_limit.TotalSeconds:F0} s"));
            return null;
        }
    }

    // The folders, links and files of the tree in the order of their paths, every file listed as carried whole.
    private static List<(UpdatePatchEntry Entry, string Path)> Walk(string tree)
    {
        var root = Path.GetFullPath(tree);
        var entries = new List<(UpdatePatchEntry Entry, string Path)>();
        var folders = new Stack<string>();
        folders.Push(root);
        while (folders.Count > 0)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folders.Pop()))
            {
                var name = Path.GetRelativePath(root, path).Replace('\\', '/');
                var info = new FileInfo(path);
                if (info.LinkTarget is { } target)
                {
                    if (target.Length == 0 || target.StartsWith('/') || target.Any(char.IsWhiteSpace))
                    {
                        throw new InvalidDataException($"the link {name} points at '{target}', which the list cannot carry");
                    }

                    entries.Add((new UpdatePatchEntry(UpdatePatchKind.Link, name, (UnixFileMode)0x1ff, Target: target), path));
                }
                else if (info.Attributes.HasFlag(FileAttributes.Directory))
                {
                    folders.Push(path);
                    if (!OperatingSystem.IsWindows())
                    {
                        entries.Add((new UpdatePatchEntry(UpdatePatchKind.Folder, name, Mode(path)), path));
                    }
                }
                else
                {
                    entries.Add((new UpdatePatchEntry(UpdatePatchKind.Whole, name, Mode(path), Digest(path), info.Length), path));
                }
            }
        }

        entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Entry.Path, right.Entry.Path));
        return entries;
    }

    // The files of the earlier tree a machine holds, by path.
    private static Dictionary<string, Earlier> Before(string earlier, IReadOnlyList<string> held)
    {
        var root = Path.GetFullPath(earlier);
        var folders = held.Select(folder => folder.Replace('\\', '/').Trim('/') + "/").ToList();
        var files = new Dictionary<string, Earlier>(StringComparer.Ordinal);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            var name = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (folders.Count == 0 || folders.Any(folder => name.StartsWith(folder, StringComparison.Ordinal)))
            {
                files[name] = new Earlier(path, Digest(path), new FileInfo(path).Length);
            }
        }

        return files;
    }

    private static async Task AddAsync(ZipArchive archive, string name, Stream source, CompressionLevel level, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, level);
        entry.LastWriteTime = _stamp;
        using var target = entry.Open();
        await source.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    private static long Packed(byte[] data)
    {
        using var output = new MemoryStream();
        using (var packer = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            packer.Write(data);
        }

        return output.Length;
    }

    private static UnixFileMode Mode(string path) =>
        OperatingSystem.IsWindows() ? Plain : File.GetUnixFileMode(path) & (UnixFileMode)0x1ff;

    private static string Digest(string path)
    {
        using var file = File.OpenRead(path);

        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    // A file of the earlier tree.
    private sealed record Earlier(string Path, string Sha256, long Size);

    // A changed file to make a delta for: its entry, where it lies and the earlier file at its path.
    private sealed record Job(UpdatePatchEntry Entry, string Path, Earlier Was);
}
