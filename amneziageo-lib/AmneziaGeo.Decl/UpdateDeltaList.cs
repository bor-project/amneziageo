using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AmneziaGeo.Decl;

/// <summary>
/// A delta beside a release asset: the file it makes, the earlier file it starts from and where it lies in the pack
/// of deltas.
/// </summary>
/// <param name="Sha256">The digest of the file the delta makes.</param>
/// <param name="Base">The digest of the earlier file.</param>
/// <param name="BaseSize">The size of the earlier file, in bytes.</param>
/// <param name="Offset">Where the delta starts in the pack of deltas.</param>
/// <param name="Length">How many bytes of the pack of deltas the delta takes.</param>
public sealed record UpdateDeltaEntry(string Sha256, string Base, long BaseSize, long Offset, long Length);

/// <summary>
/// Reads the list of deltas beside a release asset: deltas of its changed files from their versions in earlier
/// releases, one after another in the pack of deltas.
/// </summary>
public static partial class UpdateDeltaList
{
    /// <summary>
    /// The first line of a list this build reads.
    /// </summary>
    public const string Head = "# amneziageo deltas 1";

    /// <summary>
    /// Reads a list and refuses one that does not hold together or does not cover a pack of the size given.
    /// </summary>
    public static IReadOnlyList<UpdateDeltaEntry> Parse(byte[] text, long pack)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = Encoding.UTF8.GetString(text).Split('\n');
        if (lines[0] != Head)
        {
            throw new InvalidDataException("the list of deltas does not start with its head");
        }

        var entries = new List<UpdateDeltaEntry>();
        var pairs = new HashSet<(string, string)>();
        var next = 0L;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            var entry = Entry(lines[i], i + 1);
            if (entry.Offset != next)
            {
                throw new InvalidDataException($"line {i + 1} of the list of deltas does not follow the delta before in the pack");
            }

            if (!pairs.Add((entry.Sha256, entry.Base)) || entries.Count == UpdateList.MaxEntries)
            {
                throw new InvalidDataException($"line {i + 1} of the list of deltas names a delta twice or one too many");
            }

            entries.Add(entry);
            next += entry.Length;
        }

        if (entries.Count == 0 || next != pack)
        {
            throw new InvalidDataException("the list of deltas does not cover the pack");
        }

        return entries;
    }

    private static UpdateDeltaEntry Entry(string line, int number)
    {
        var fields = line.Split(' ');
        return fields.Length == 5
            && Digest().IsMatch(fields[0])
            && Digest().IsMatch(fields[1])
            && fields[0] != fields[1]
            && Count(fields[2], out var baseSize)
            && Count(fields[3], out var offset)
            && Count(fields[4], out var length)
            && length > 0
            && baseSize <= UpdateDelta.MaxSize
            && length <= UpdateList.MaxFileSize
                ? new UpdateDeltaEntry(fields[0], fields[1], baseSize, offset, length)
                : throw new InvalidDataException($"line {number} of the list of deltas does not read");
    }

    private static bool Count(string text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Digest();
}
