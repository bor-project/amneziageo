using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Holds the image every program was started with, so one that has already exited is still named when its traffic
/// is decided. The starts are read from the same session the network events come from, which is what puts a start
/// ahead of the packets that follow it.
/// </summary>
internal sealed class ProcessImages
{
    // Microsoft-Windows-Kernel-Process.
    public static readonly Guid Provider = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
    // WINEVENT_KEYWORD_PROCESS.
    public const ulong Keyword = 0x10UL;
    public const int StartId = 1;

    // Payload (little-endian): pid(4) sequence(8) created(8) parent(4) parent sequence(8) session(4) flags(4)
    // elevation(4) elevated(4) label(SID, its own length) image(unicode). The label is the only variable part
    // before the image, and its length is the count of subauthorities it carries.
    private const int CreatedOffset = 12;
    private const int LabelOffset = 48;
    private const int LabelHeaderBytes = 8;
    private const int SubAuthorityCountOffset = 1;
    private const int SubAuthorityBytes = 4;
    // A busy machine starts a few dozen programs a second, and only the recent ones are ever asked about.
    private const int Capacity = 8192;
    private const int Kept = 4096;

    private readonly ConcurrentDictionary<uint, Held> _images = new();
    private long _order;

    private readonly record struct Held(string Path, long Created, long Order);

    /// <summary>
    /// The image one process was started with, whether or not it still runs.
    /// </summary>
    public bool TryGet(uint pid, out (string Path, long Created) image)
    {
        if (_images.TryGetValue(pid, out var held))
        {
            image = (held.Path, held.Created);
            return true;
        }

        image = default;
        return false;
    }

    /// <summary>
    /// Holds the image of one started program.
    /// </summary>
    public void Note(TraceEvent evt)
    {
        if (TryRead(evt.EventData(), out var pid, out var path, out var created))
        {
            Remember(pid, path, created);
        }
    }

    /// <summary>
    /// Reads the pid, the image and the start time out of the payload of one start.
    /// </summary>
    internal static bool TryRead(byte[]? data, out uint pid, out string path, out long created)
    {
        pid = 0;
        path = string.Empty;
        created = 0;
        if (data is null || data.Length <= LabelOffset + SubAuthorityCountOffset)
        {
            return false;
        }

        pid = BitConverter.ToUInt32(data, 0);
        if (pid == 0)
        {
            return false;
        }

        var start = LabelOffset + LabelHeaderBytes + (SubAuthorityBytes * data[LabelOffset + SubAuthorityCountOffset]);
        if (data.Length <= start)
        {
            return false;
        }

        var text = System.Text.Encoding.Unicode.GetString(data, start, data.Length - start);
        var end = text.IndexOf('\0');
        path = end >= 0 ? text[..end] : text;
        // A payload laid out differently would leave anything here, and only a path can be matched against a rule.
        if (path.Length == 0 || !path.Contains('\\'))
        {
            path = string.Empty;
            return false;
        }

        created = BitConverter.ToInt64(data, CreatedOffset);
        return true;
    }

    /// <summary>
    /// Holds one image against its pid.
    /// </summary>
    internal void Remember(uint pid, string path, long created)
    {
        _images[pid] = new Held(path, created, Interlocked.Increment(ref _order));
        if (_images.Count > Capacity)
        {
            Trim();
        }
    }

    // Drops the images held longest when too many have gathered.
    private void Trim()
    {
        var cutoff = Interlocked.Read(ref _order) - Kept;
        foreach (var pair in _images)
        {
            if (pair.Value.Order <= cutoff)
            {
                _images.TryRemove(pair.Key, out _);
            }
        }
    }
}
