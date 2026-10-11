using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AmneziaGeo.Decl;

/// <summary>
/// What an entry of the list of a patch is.
/// </summary>
public enum UpdatePatchKind
{
    /// <summary>
    /// A file the machine holds.
    /// </summary>
    Held,

    /// <summary>
    /// A file the patch carries whole.
    /// </summary>
    Whole,

    /// <summary>
    /// A file the patch makes from a file the machine holds.
    /// </summary>
    Delta,

    /// <summary>
    /// A folder.
    /// </summary>
    Folder,

    /// <summary>
    /// A symbolic link.
    /// </summary>
    Link,
}

/// <summary>
/// An entry of the list of a patch.
/// </summary>
/// <param name="Kind">What the entry is.</param>
/// <param name="Path">Where the entry lies in the asset, parts divided by a slash.</param>
/// <param name="Mode">The permissions of the entry.</param>
/// <param name="Sha256">The digest of a file.</param>
/// <param name="Size">The size of a file, in bytes.</param>
/// <param name="Base">The digest of the held file a delta starts from.</param>
/// <param name="BaseSize">The size of that file, in bytes.</param>
/// <param name="Target">What a link points at.</param>
public sealed record UpdatePatchEntry(
    UpdatePatchKind Kind,
    string Path,
    UnixFileMode Mode,
    string Sha256 = "",
    long Size = 0,
    string Base = "",
    long BaseSize = 0,
    string Target = "");

/// <summary>
/// What putting an asset together from a patch took.
/// </summary>
/// <param name="Files">How many files the asset holds.</param>
/// <param name="Carried">How many of them came from the patch, whole or as deltas.</param>
/// <param name="Bytes">The size of the patch, in bytes.</param>
/// <param name="Deltas">How many of the files carried were made from a delta and a file the machine holds.</param>
public sealed record UpdatePatchCount(int Files, int Carried, long Bytes, int Deltas);

/// <summary>
/// The patch beside a release asset: an archive that makes the files of the asset out of the files of one earlier
/// version. Its list names every file of the asset as held by a machine with that version, carried whole or made from
/// a delta and a held file.
/// </summary>
public static partial class UpdatePatch
{
    /// <summary>
    /// The first line of a list this build reads.
    /// </summary>
    public const string Head = "# amneziageo patch 1";

    /// <summary>
    /// The entry of the archive that holds the list.
    /// </summary>
    public const string ListName = "list";

    /// <summary>
    /// The folder of the archive that holds the files carried whole, each under its digest.
    /// </summary>
    public const string WholeFolder = "files/";

    /// <summary>
    /// The folder of the archive that holds the deltas, each under the digest of the file it makes.
    /// </summary>
    public const string DeltaFolder = "deltas/";

    /// <summary>
    /// What a downloaded patch is named with after the folder it makes.
    /// </summary>
    public const string Extension = ".patch";

    /// <summary>
    /// The largest list read, in bytes.
    /// </summary>
    public const int MaxListSize = 4 * 1024 * 1024;

    /// <summary>
    /// The most entries a list names.
    /// </summary>
    public const int MaxEntries = 20000;

    /// <summary>
    /// The largest patch and the largest file of one, in bytes.
    /// </summary>
    public const long MaxFileSize = 512L * 1024 * 1024;

    private const UnixFileMode FolderMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>
    /// Downloads the patch beside the folder, carrying on from an earlier attempt, checks it against the digest the
    /// manifest names and puts the asset into the folder from it and the files found under the folders held.
    /// Progress hears the bytes on disk and the size of the patch.
    /// </summary>
    public static async Task<UpdatePatchCount> FetchAsync(
        HttpClient http,
        Uri address,
        UpdatePatchFile patch,
        IReadOnlyList<string> held,
        string target,
        Action<long, long>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(held);

        if (patch is not { Size: > 0 and <= MaxFileSize, Sha256: { Length: > 0 } digest })
        {
            throw new InvalidDataException("the release names no patch of a size it may have");
        }

        var path = target + Extension;
        if (!Matches(path, patch.Size, digest))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await UpdateDownload.DownloadAsync(http, address, path, progress, ct).ConfigureAwait(false);
            if (!Matches(path, patch.Size, digest))
            {
                File.Delete(path);
                throw new InvalidDataException($"{patch.Name} does not match the digest the manifest names");
            }
        }

        return await ApplyAsync(path, held, target, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the asset into the folder from the patch and the files found under the folders held, checking every file
    /// against the list of the patch. A machine that lacks a file the patch counts on is refused.
    /// </summary>
    public static async Task<UpdatePatchCount> ApplyAsync(string path, IReadOnlyList<string> held, string target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(held);

        using var archive = ZipFile.OpenRead(path);
        var entries = Parse(Body(archive, ListName, MaxListSize));
        var files = entries
            .Where(entry => entry.Kind is UpdatePatchKind.Held or UpdatePatchKind.Whole or UpdatePatchKind.Delta)
            .ToList();
        var found = Holdings(held, files);
        foreach (var entry in files)
        {
            var wanted = entry.Kind switch
            {
                UpdatePatchKind.Held => entry.Sha256,
                UpdatePatchKind.Delta => entry.Base,
                _ => null,
            };
            if (wanted is not null && !found.ContainsKey(wanted))
            {
                throw new InvalidDataException($"the machine does not hold what {entry.Path} is made from");
            }
        }

        Remove(target);
        Directory.CreateDirectory(target);
        var made = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in files)
        {
            var place = Place(target, entry.Path);
            if (entry.Kind == UpdatePatchKind.Held)
            {
                await CopyAsync(found[entry.Sha256], entry, place, ct).ConfigureAwait(false);
            }
            else if (made.TryGetValue(entry.Sha256, out var first))
            {
                await CopyAsync(first, entry, place, ct).ConfigureAwait(false);
            }
            else if (entry.Kind == UpdatePatchKind.Delta)
            {
                await PatchAsync(archive, entry, found[entry.Base], place, ct).ConfigureAwait(false);
                made[entry.Sha256] = place;
            }
            else
            {
                await UnpackAsync(archive, entry, place, ct).ConfigureAwait(false);
                made[entry.Sha256] = place;
            }
        }

        foreach (var entry in entries.Where(entry => entry.Kind == UpdatePatchKind.Folder))
        {
            var place = Place(target, entry.Path);
            MakeFolder(place);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(place, entry.Mode);
            }
        }

        foreach (var entry in entries.Where(entry => entry.Kind == UpdatePatchKind.Link))
        {
            if (OperatingSystem.IsWindows())
            {
                throw new InvalidDataException($"the list of the patch names the link {entry.Path}");
            }

            var place = Place(target, entry.Path);
            MakeFolder(Path.GetDirectoryName(place)!);
            File.CreateSymbolicLink(place, entry.Target);
        }

        return new UpdatePatchCount(
            files.Count,
            files.Count(entry => entry.Kind != UpdatePatchKind.Held),
            new FileInfo(path).Length,
            files.Count(entry => entry.Kind == UpdatePatchKind.Delta));
    }

    /// <summary>
    /// Reads the list of a patch on disk.
    /// </summary>
    public static IReadOnlyList<UpdatePatchEntry> ReadList(string path)
    {
        using var archive = ZipFile.OpenRead(path);

        return Parse(Body(archive, ListName, MaxListSize));
    }

    /// <summary>
    /// Whether the file is of the size and digest given.
    /// </summary>
    public static bool Matches(string path, long size, string digest)
    {
        var file = new FileInfo(path);

        return file.Exists && file.Length == size && string.Equals(Digest(path), digest, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads a list and refuses one that does not hold together.
    /// </summary>
    public static IReadOnlyList<UpdatePatchEntry> Parse(byte[] text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = Encoding.UTF8.GetString(text).Split('\n');
        if (lines[0] != Head)
        {
            throw new InvalidDataException("the list of the patch does not start with its head");
        }

        var entries = new List<UpdatePatchEntry>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var ways = new Dictionary<string, (UpdatePatchKind Kind, string Base)>(StringComparer.Ordinal);
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            var entry = Entry(lines[i], i + 1);
            if (!paths.Add(entry.Path) || entries.Count == MaxEntries)
            {
                throw new InvalidDataException($"line {i + 1} of the list of the patch names a path twice or one too many");
            }

            if (entry.Sha256.Length > 0)
            {
                if (ways.TryGetValue(entry.Sha256, out var way) && way != (entry.Kind, entry.Base))
                {
                    throw new InvalidDataException($"line {i + 1} of the list of the patch makes a file another way than a line before");
                }

                ways[entry.Sha256] = (entry.Kind, entry.Base);
            }

            entries.Add(entry);
        }

        if (ways.Count == 0)
        {
            throw new InvalidDataException("the list of the patch names no file");
        }

        foreach (var link in entries.Where(entry => entry.Kind == UpdatePatchKind.Link))
        {
            if (paths.Any(path => path.StartsWith(link.Path + "/", StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"the list of the patch puts a path under the link {link.Path}");
            }
        }

        return entries;
    }

    /// <summary>
    /// The line of an entry in a list.
    /// </summary>
    public static string Line(UpdatePatchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var mode = Convert.ToString((int)entry.Mode & 0x1ff, 8).PadLeft(3, '0');
        return entry.Kind switch
        {
            UpdatePatchKind.Held => string.Create(CultureInfo.InvariantCulture, $"= {entry.Sha256} {mode} {entry.Size} {entry.Path}"),
            UpdatePatchKind.Whole => string.Create(CultureInfo.InvariantCulture, $"+ {entry.Sha256} {mode} {entry.Size} {entry.Path}"),
            UpdatePatchKind.Delta => string.Create(
                CultureInfo.InvariantCulture,
                $"~ {entry.Sha256} {mode} {entry.Size} {entry.Base} {entry.BaseSize} {entry.Path}"),
            UpdatePatchKind.Folder => $"d {mode} {entry.Path}",
            _ => $"l {entry.Target} {entry.Path}",
        };
    }

    private static UpdatePatchEntry Entry(string line, int number)
    {
        var fields = line.Split(' ', line[0] switch
        {
            '~' => 7,
            'd' or 'l' => 3,
            _ => 5,
        });
        return fields[0] switch
        {
            "=" when Reads(fields, 5, out var size) =>
                new UpdatePatchEntry(UpdatePatchKind.Held, fields[4], Permissions(fields[2]), fields[1], size),
            "+" when Reads(fields, 5, out var size) =>
                new UpdatePatchEntry(UpdatePatchKind.Whole, fields[4], Permissions(fields[2]), fields[1], size),
            "~" when Reads(fields, 7, out var size)
                && Digest().IsMatch(fields[4])
                && fields[4] != fields[1]
                && Count(fields[5], out var baseSize)
                && size <= UpdateDelta.MaxSize
                && baseSize <= UpdateDelta.MaxSize =>
                new UpdatePatchEntry(UpdatePatchKind.Delta, fields[6], Permissions(fields[2]), fields[1], size, fields[4], baseSize),
            "d" when fields.Length == 3 && Mode().IsMatch(fields[1]) && Safe(fields[2]) =>
                new UpdatePatchEntry(UpdatePatchKind.Folder, fields[2], Permissions(fields[1])),
            "l" when fields.Length == 3 && Target(fields[1]) && Safe(fields[2]) =>
                new UpdatePatchEntry(UpdatePatchKind.Link, fields[2], (UnixFileMode)0x1ff, Target: fields[1]),
            _ => throw new InvalidDataException($"line {number} of the list of the patch does not read"),
        };
    }

    // Whether the fields of a file line read: its digest, mode and size after the kind, its path last.
    private static bool Reads(string[] fields, int count, out long size)
    {
        size = 0;
        return fields.Length == count
            && Digest().IsMatch(fields[1])
            && Mode().IsMatch(fields[2])
            && Count(fields[3], out size)
            && size <= MaxFileSize
            && Safe(fields[^1]);
    }

    private static Dictionary<string, string> Holdings(IReadOnlyList<string> held, IReadOnlyList<UpdatePatchEntry> files)
    {
        var sizes = new HashSet<long>();
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in files)
        {
            if (entry.Kind == UpdatePatchKind.Held)
            {
                sizes.Add(entry.Size);
                wanted.Add(entry.Sha256);
            }
            else if (entry.Kind == UpdatePatchKind.Delta)
            {
                sizes.Add(entry.BaseSize);
                wanted.Add(entry.Base);
            }
        }

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

    // An entry of the archive whole, refused when it is absent or larger than the size given.
    private static byte[] Body(ZipArchive archive, string name, long most)
    {
        if (archive.GetEntry(name) is not { } entry || entry.Length > most)
        {
            throw new InvalidDataException($"the patch carries no {name} of a size it may have");
        }

        using var source = entry.Open();
        var data = new byte[entry.Length];
        source.ReadExactly(data);
        if (source.ReadByte() >= 0)
        {
            throw new InvalidDataException($"{name} of the patch is larger than the patch says");
        }

        return data;
    }

    private static async Task UnpackAsync(ZipArchive archive, UpdatePatchEntry entry, string path, CancellationToken ct)
    {
        if (archive.GetEntry(WholeFolder + entry.Sha256) is not { } body || body.Length != entry.Size)
        {
            throw new InvalidDataException($"the patch does not carry {entry.Path} of the size its list names");
        }

        MakeFolder(Path.GetDirectoryName(path)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var source = body.Open())
        using (var file = File.Create(path))
        {
            var chunk = new byte[81920];
            var total = 0L;
            var read = 0;
            while ((read = await source.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
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

    private static async Task PatchAsync(ZipArchive archive, UpdatePatchEntry entry, string source, string path, CancellationToken ct)
    {
        var delta = Body(archive, DeltaFolder + entry.Sha256, MaxFileSize);
        MakeFolder(Path.GetDirectoryName(path)!);
        var earlier = await File.ReadAllBytesAsync(source, ct).ConfigureAwait(false);
        using (var file = File.Create(path))
        {
            UpdateDelta.Apply(earlier, delta, file, entry.Size);
        }

        Seal(path, entry, Digest(path));
    }

    private static async Task CopyAsync(string source, UpdatePatchEntry entry, string path, CancellationToken ct)
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

    private static void Seal(string path, UpdatePatchEntry entry, string digest)
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

    private static string Digest(string path)
    {
        using var file = File.OpenRead(path);

        return Convert.ToHexStringLower(SHA256.HashData(file));
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

        Directory.CreateDirectory(path, FolderMode);
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

    private static UnixFileMode Permissions(string text) => (UnixFileMode)(Convert.ToInt32(text, 8) & 0x1ff);

    private static bool Count(string text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool Safe(string path) =>
        path.Length > 0
        && !path.StartsWith('/')
        && !path.Any(char.IsControl)
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Contains(':', StringComparison.Ordinal)
        && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

    private static bool Target(string target) =>
        target.Length > 0
        && !target.StartsWith('/')
        && !target.Any(char.IsControl)
        && !target.Contains('\\', StringComparison.Ordinal);

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Digest();

    [GeneratedRegex("^[0-7]{3,4}$")]
    private static partial Regex Mode();
}
