using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AmneziaGeo.Decl;

/// <summary>
/// What an entry of the list of files of a release asset is.
/// </summary>
public enum UpdateEntryKind
{
    /// <summary>
    /// A file packed in the pack.
    /// </summary>
    File,

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
/// An entry of the list of files of a release asset.
/// </summary>
/// <param name="Kind">What the entry is.</param>
/// <param name="Path">Where the entry lies in the asset, parts divided by a slash.</param>
/// <param name="Mode">The permissions of the entry.</param>
/// <param name="Sha256">The digest of a file.</param>
/// <param name="Size">The size of a file, in bytes.</param>
/// <param name="Offset">Where the packed file starts in the pack.</param>
/// <param name="Length">How many bytes of the pack the packed file takes.</param>
/// <param name="Target">What a link points at.</param>
public sealed record UpdateEntry(
    UpdateEntryKind Kind,
    string Path,
    UnixFileMode Mode,
    string Sha256 = "",
    long Size = 0,
    long Offset = 0,
    long Length = 0,
    string Target = "");

/// <summary>
/// Reads the list of files beside a release asset: every file packed on its own one after another in the pack, and
/// the folders and links of a package.
/// </summary>
public static partial class UpdateList
{
    /// <summary>
    /// The first line of a list this build reads.
    /// </summary>
    public const string Head = "# amneziageo files 1";

    /// <summary>
    /// The largest list read, in bytes.
    /// </summary>
    public const int MaxSize = 4 * 1024 * 1024;

    /// <summary>
    /// The most entries a list names.
    /// </summary>
    public const int MaxEntries = 20000;

    /// <summary>
    /// The largest file, packed or not, an update takes, in bytes.
    /// </summary>
    public const long MaxFileSize = 512L * 1024 * 1024;

    /// <summary>
    /// Reads a list and refuses one that does not hold together or does not cover a pack of the size given.
    /// </summary>
    public static IReadOnlyList<UpdateEntry> Parse(byte[] text, long pack)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = Encoding.UTF8.GetString(text).Split('\n');
        if (lines[0] != Head)
        {
            throw new InvalidDataException("the list of files does not start with its head");
        }

        var entries = new List<UpdateEntry>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var next = 0L;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            var entry = Entry(lines[i], i + 1);
            if (entry.Kind == UpdateEntryKind.File && entry.Offset != next)
            {
                throw new InvalidDataException($"line {i + 1} of the list of files does not follow the file before in the pack");
            }

            if (!paths.Add(entry.Path) || entries.Count == MaxEntries)
            {
                throw new InvalidDataException($"line {i + 1} of the list of files names a path twice or one too many");
            }

            entries.Add(entry);
            next += entry.Length;
        }

        if (!entries.Any(entry => entry.Kind == UpdateEntryKind.File) || next != pack)
        {
            throw new InvalidDataException("the list of files does not cover the pack");
        }

        foreach (var link in entries.Where(entry => entry.Kind == UpdateEntryKind.Link))
        {
            if (paths.Any(path => path.StartsWith(link.Path + "/", StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"the list of files puts a path under the link {link.Path}");
            }
        }

        return entries;
    }

    private static UpdateEntry Entry(string line, int number)
    {
        var fields = line.Split(' ', line.StartsWith("d ", StringComparison.Ordinal) || line.StartsWith("l ", StringComparison.Ordinal) ? 3 : 6);
        return fields[0] switch
        {
            "d" when fields.Length == 3 && Mode().IsMatch(fields[1]) && Safe(fields[2]) =>
                new UpdateEntry(UpdateEntryKind.Folder, fields[2], Permissions(fields[1])),
            "l" when fields.Length == 3 && Target(fields[1]) && Safe(fields[2]) =>
                new UpdateEntry(UpdateEntryKind.Link, fields[2], (UnixFileMode)0x1ff, Target: fields[1]),
            _ when fields.Length == 6
                && Digest().IsMatch(fields[0])
                && Mode().IsMatch(fields[1])
                && Count(fields[2], out var size)
                && Count(fields[3], out var offset)
                && Count(fields[4], out var length)
                && length > 0
                && size <= MaxFileSize
                && length <= MaxFileSize
                && Safe(fields[5]) =>
                new UpdateEntry(UpdateEntryKind.File, fields[5], Permissions(fields[1]), fields[0], size, offset, length),
            _ => throw new InvalidDataException($"line {number} of the list of files does not read"),
        };
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
