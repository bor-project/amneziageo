using System.Buffers.Binary;
using System.Text;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Reads the target a Windows shortcut file points at on a drive of the machine.
/// </summary>
internal static class ShellLink
{
    private const int HeaderSize = 0x4C;

    private const uint HasTargetIdList = 0x1;

    private const uint HasLinkInfo = 0x2;

    private const uint VolumeIdAndLocalBasePath = 0x1;

    /// <summary>
    /// Returns the path a shortcut file points at, null when it names none on a drive of the machine.
    /// </summary>
    public static string? Target(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Length is > HeaderSize and < 1024 * 1024 ? Target(File.ReadAllBytes(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the path the bytes of a shortcut point at, null when they name none on a drive of the machine.
    /// </summary>
    public static string? Target(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != HeaderSize)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]);
        var at = HeaderSize;
        if ((flags & HasTargetIdList) != 0)
        {
            if (at + 2 > data.Length)
            {
                return null;
            }

            at += 2 + BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
        }

        if ((flags & HasLinkInfo) == 0 || at + 0x1C > data.Length)
        {
            return null;
        }

        var info = data[at..];
        var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(info), (uint)info.Length);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
        var infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
        if ((infoFlags & VolumeIdAndLocalBasePath) == 0)
        {
            return null;
        }

        var block = info[..size];
        var unicode = headerSize >= 0x24 && size >= 0x24 ? BinaryPrimitives.ReadUInt32LittleEndian(info[0x1C..]) : 0u;
        var path = unicode > 0
            ? Unicode(block, (int)unicode) + Unicode(block, (int)BinaryPrimitives.ReadUInt32LittleEndian(info[0x20..]))
            : Joined(Ascii(block, (int)BinaryPrimitives.ReadUInt32LittleEndian(info[0x10..])), Ascii(block, (int)BinaryPrimitives.ReadUInt32LittleEndian(info[0x18..])));

        return path is { Length: > 2 } && path[1] == ':' ? path : null;
    }

    // The base path and its suffix, none when either could not be read.
    private static string? Joined(string? path, string? suffix) => path is null || suffix is null ? null : path + suffix;

    // A string of 8-bit letters ended by a zero, read only when it is plain ASCII.
    private static string? Ascii(ReadOnlySpan<byte> block, int offset)
    {
        if (offset <= 0 || offset >= block.Length)
        {
            return string.Empty;
        }

        var end = block[offset..].IndexOf((byte)0);
        var text = end < 0 ? block[offset..] : block.Slice(offset, end);

        return text.ContainsAnyExceptInRange((byte)0x20, (byte)0x7E) ? null : Encoding.ASCII.GetString(text);
    }

    // A string of 16-bit letters ended by a zero.
    private static string Unicode(ReadOnlySpan<byte> block, int offset)
    {
        if (offset <= 0 || offset >= block.Length)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        for (var at = offset; at + 1 < block.Length; at += 2)
        {
            var letter = (char)BinaryPrimitives.ReadUInt16LittleEndian(block[at..]);
            if (letter == '\0')
            {
                break;
            }

            text.Append(letter);
        }

        return text.ToString();
    }
}
