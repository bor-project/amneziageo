using System.Buffers.Binary;
using System.IO.Compression;

namespace AmneziaGeo.Decl;

/// <summary>
/// A binary delta that makes the current version of a file from an earlier one, in the manner of bsdiff: stretches
/// close to the earlier file go as byte differences, the rest as new bytes, each stream compressed with Brotli.
/// </summary>
public static class UpdateDelta
{
    /// <summary>
    /// The largest file, earlier or current, a delta is made for, in bytes.
    /// </summary>
    public const int MaxSize = 256 * 1024 * 1024;

    private const int HeadSize = 40;
    private const int RecordSize = 24;
    private const int Slack = 8;
    private const int Quality = 11;
    private const int Window = 24;

    private static ReadOnlySpan<byte> Magic => "AGDELTA1"u8;

    /// <summary>
    /// Makes the delta from the earlier file to the current one.
    /// </summary>
    public static byte[] Create(ReadOnlySpan<byte> earlier, ReadOnlySpan<byte> current, CancellationToken ct = default)
    {
        if (earlier.Length > MaxSize || current.Length > MaxSize)
        {
            throw new ArgumentException("a file past the size a delta takes");
        }

        var order = Suffixes(earlier, ct);
        var differences = new byte[current.Length];
        var added = new byte[current.Length];
        var control = new List<long>();
        var (differenceCount, addedCount) = Scan(earlier, order, current, control, differences, added, ct);

        var records = new byte[control.Count * sizeof(long)];
        for (var i = 0; i < control.Count; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(records.AsSpan(i * sizeof(long)), control[i]);
        }

        var packedControl = Compress(records);
        var packedDifferences = Compress(differences.AsSpan(0, differenceCount));
        var packedAdded = Compress(added.AsSpan(0, addedCount));
        var delta = new byte[HeadSize + packedControl.Length + packedDifferences.Length + packedAdded.Length];
        Magic.CopyTo(delta);
        BinaryPrimitives.WriteInt64LittleEndian(delta.AsSpan(8), current.Length);
        BinaryPrimitives.WriteInt64LittleEndian(delta.AsSpan(16), packedControl.Length);
        BinaryPrimitives.WriteInt64LittleEndian(delta.AsSpan(24), packedDifferences.Length);
        BinaryPrimitives.WriteInt64LittleEndian(delta.AsSpan(32), packedAdded.Length);
        packedControl.CopyTo(delta, HeadSize);
        packedDifferences.CopyTo(delta, HeadSize + packedControl.Length);
        packedAdded.CopyTo(delta, HeadSize + packedControl.Length + packedDifferences.Length);
        return delta;
    }

    /// <summary>
    /// Writes the file a delta makes from the earlier one and refuses a delta that does not hold together or does not
    /// make a file of the size given.
    /// </summary>
    public static void Apply(ReadOnlySpan<byte> earlier, byte[] delta, Stream output, long size)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(output);

        if (delta.Length < HeadSize || !delta.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("the delta does not start with its head");
        }

        var target = BinaryPrimitives.ReadInt64LittleEndian(delta.AsSpan(8));
        var controlLength = BinaryPrimitives.ReadInt64LittleEndian(delta.AsSpan(16));
        var differenceLength = BinaryPrimitives.ReadInt64LittleEndian(delta.AsSpan(24));
        var addedLength = BinaryPrimitives.ReadInt64LittleEndian(delta.AsSpan(32));
        if (target != size
            || controlLength is < 0 or > int.MaxValue
            || differenceLength is < 0 or > int.MaxValue
            || addedLength is < 0 or > int.MaxValue
            || HeadSize + controlLength + differenceLength + addedLength != delta.Length)
        {
            throw new InvalidDataException("the delta does not hold together");
        }

        try
        {
            using var control = Open(delta, HeadSize, controlLength);
            using var differences = Open(delta, HeadSize + controlLength, differenceLength);
            using var added = Open(delta, HeadSize + controlLength + differenceLength, addedLength);
            Replay(earlier, control, differences, added, output, size);
        }
        catch (Exception ex) when (ex is InvalidOperationException or EndOfStreamException)
        {
            throw new InvalidDataException("the delta does not read", ex);
        }
    }

    private static void Replay(ReadOnlySpan<byte> earlier, Stream control, Stream differences, Stream added, Stream output, long size)
    {
        var record = new byte[RecordSize];
        var chunk = new byte[81920];
        var written = 0L;
        var at = 0L;
        var records = 0L;
        while (written < size)
        {
            if (++records > size + 2)
            {
                throw new InvalidDataException("the delta runs on without making the file");
            }

            control.ReadExactly(record);
            var copy = BinaryPrimitives.ReadInt64LittleEndian(record);
            var extra = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(8));
            var seek = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(16));
            if (copy < 0 || extra < 0 || copy > size - written || extra > size - written - copy
                || at < 0 || copy > earlier.Length - at || seek < -(earlier.Length + size) || seek > earlier.Length + size)
            {
                throw new InvalidDataException("the delta steps outside the files");
            }

            for (var left = copy; left > 0;)
            {
                var take = (int)Math.Min(left, chunk.Length);
                differences.ReadExactly(chunk.AsSpan(0, take));
                var from = earlier.Slice((int)at, take);
                for (var i = 0; i < take; i++)
                {
                    chunk[i] += from[i];
                }

                output.Write(chunk, 0, take);
                at += take;
                left -= take;
            }

            for (var left = extra; left > 0;)
            {
                var take = (int)Math.Min(left, chunk.Length);
                added.ReadExactly(chunk.AsSpan(0, take));
                output.Write(chunk, 0, take);
                left -= take;
            }

            written += copy + extra;
            at += seek;
        }

        if (control.Read(record) > 0 || differences.ReadByte() >= 0 || added.ReadByte() >= 0)
        {
            throw new InvalidDataException("the delta runs on past the file it makes");
        }
    }

    private static BrotliStream Open(byte[] delta, long offset, long length) =>
        new(new MemoryStream(delta, (int)offset, (int)length, writable: false), CompressionMode.Decompress);

    private static byte[] Compress(ReadOnlySpan<byte> data)
    {
        var packed = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length)];
        if (!BrotliEncoder.TryCompress(data, packed, out var written, Quality, Window))
        {
            throw new InvalidOperationException("Brotli did not compress the delta");
        }

        return packed[..written];
    }

    // Matches the current file against the earlier one: exact matches found through the suffixes of the earlier file,
    // stretched over the bytes around them that mostly agree.
    private static (int Differences, int Added) Scan(
        ReadOnlySpan<byte> old,
        int[] order,
        ReadOnlySpan<byte> current,
        List<long> control,
        byte[] differences,
        byte[] added,
        CancellationToken ct)
    {
        var n = old.Length;
        var m = current.Length;
        var differenceCount = 0;
        var addedCount = 0;
        var scan = 0;
        var length = 0;
        var position = 0;
        var lastScan = 0;
        var lastPosition = 0;
        var lastOffset = 0;
        var steps = 0;
        while (scan < m)
        {
            var oldScore = 0;
            scan += length;
            var scored = scan;
            for (; scan < m; scan++)
            {
                if ((++steps & 0xfff) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                length = Longest(old, order, current[scan..], out position);
                for (; scored < scan + length; scored++)
                {
                    if (Agrees(old, current, scored + lastOffset, scored))
                    {
                        oldScore++;
                    }
                }

                if ((length == oldScore && length != 0) || length > oldScore + Slack)
                {
                    break;
                }

                if (Agrees(old, current, scan + lastOffset, scan))
                {
                    oldScore--;
                }
            }

            if (length == oldScore && scan != m)
            {
                continue;
            }

            var forward = Forward(old, current, lastScan, lastPosition, scan);
            var backward = scan < m ? Backward(old, current, lastScan, scan, position) : 0;
            if (lastScan + forward > scan - backward)
            {
                var overlap = lastScan + forward - (scan - backward);
                var shift = Split(old, current, lastScan + forward - overlap, lastPosition + forward - overlap, scan - backward, position - backward, overlap);
                forward += shift - overlap;
                backward -= shift;
            }

            for (var i = 0; i < forward; i++)
            {
                differences[differenceCount++] = (byte)(current[lastScan + i] - old[lastPosition + i]);
            }

            var extra = scan - backward - (lastScan + forward);
            current.Slice(lastScan + forward, extra).CopyTo(added.AsSpan(addedCount));
            addedCount += extra;
            control.Add(forward);
            control.Add(extra);
            control.Add(position - backward - (lastPosition + forward));
            lastScan = scan - backward;
            lastPosition = position - backward;
            lastOffset = position - scan;
        }

        return (differenceCount, addedCount);
    }

    private static bool Agrees(ReadOnlySpan<byte> old, ReadOnlySpan<byte> current, int at, int scan) =>
        at >= 0 && at < old.Length && old[at] == current[scan];

    // How far past the last match the earlier file keeps agreeing with the current one on at least half the bytes.
    private static int Forward(ReadOnlySpan<byte> old, ReadOnlySpan<byte> current, int lastScan, int lastPosition, int scan)
    {
        var same = 0;
        var best = 0;
        var length = 0;
        for (var i = 0; lastScan + i < scan && lastPosition + i < old.Length;)
        {
            if (old[lastPosition + i] == current[lastScan + i])
            {
                same++;
            }

            i++;
            if (same * 2 - i > best * 2 - length)
            {
                best = same;
                length = i;
            }
        }

        return length;
    }

    // How far before the next match the earlier file agrees with the current one on at least half the bytes.
    private static int Backward(ReadOnlySpan<byte> old, ReadOnlySpan<byte> current, int lastScan, int scan, int position)
    {
        var same = 0;
        var best = 0;
        var length = 0;
        for (var i = 1; scan >= lastScan + i && position >= i; i++)
        {
            if (old[position - i] == current[scan - i])
            {
                same++;
            }

            if (same * 2 - i > best * 2 - length)
            {
                best = same;
                length = i;
            }
        }

        return length;
    }

    // Where the stretch after the last match hands over to the stretch before the next one when the two overlap.
    private static int Split(ReadOnlySpan<byte> old, ReadOnlySpan<byte> current, int forwardScan, int forwardPosition, int backwardScan, int backwardPosition, int overlap)
    {
        var score = 0;
        var best = 0;
        var shift = 0;
        for (var i = 0; i < overlap; i++)
        {
            if (current[forwardScan + i] == old[forwardPosition + i])
            {
                score++;
            }

            if (current[backwardScan + i] == old[backwardPosition + i])
            {
                score--;
            }

            if (score > best)
            {
                best = score;
                shift = i + 1;
            }
        }

        return shift;
    }

    // The longest prefix of the probe found in the earlier file, and where it starts there.
    private static int Longest(ReadOnlySpan<byte> old, int[] order, ReadOnlySpan<byte> probe, out int position)
    {
        position = 0;
        if (order.Length == 0 || probe.IsEmpty)
        {
            return 0;
        }

        var low = 0;
        var high = order.Length - 1;
        var lowCommon = old[order[low]..].CommonPrefixLength(probe);
        var highCommon = old[order[high]..].CommonPrefixLength(probe);
        while (high - low > 1)
        {
            var middle = low + ((high - low) / 2);
            var skip = Math.Min(lowCommon, highCommon);
            var suffix = old[order[middle]..];
            var common = skip + suffix[skip..].CommonPrefixLength(probe[skip..]);
            if (common == probe.Length || (common < suffix.Length && suffix[common] > probe[common]))
            {
                high = middle;
                highCommon = common;
            }
            else
            {
                low = middle;
                lowCommon = common;
            }
        }

        if (lowCommon >= highCommon)
        {
            position = order[low];
            return lowCommon;
        }

        position = order[high];
        return highCommon;
    }

    // Sorts the suffixes of the data by doubling the prefix they are sorted on, refining only the groups still tied.
    internal static int[] Suffixes(ReadOnlySpan<byte> data, CancellationToken ct)
    {
        var n = data.Length;
        var order = new int[n];
        var rank = new int[n];
        var starts = new int[257];
        foreach (var value in data)
        {
            starts[value + 1]++;
        }

        for (var i = 1; i < starts.Length; i++)
        {
            starts[i] += starts[i - 1];
        }

        var next = (int[])starts.Clone();
        for (var i = 0; i < n; i++)
        {
            order[next[data[i]]++] = i;
        }

        var groups = new List<(int Start, int Length)>();
        for (var value = 0; value < 256; value++)
        {
            var start = starts[value];
            var end = starts[value + 1];
            for (var j = start; j < end; j++)
            {
                rank[order[j]] = end - 1;
            }

            if (end - start > 1)
            {
                groups.Add((start, end - start));
            }
        }

        var keys = new int[n];
        for (var h = 1L; groups.Count > 0; h *= 2)
        {
            ct.ThrowIfCancellationRequested();
            var tied = new List<(int Start, int Length)>();
            foreach (var (start, length) in groups)
            {
                for (var j = start; j < start + length; j++)
                {
                    var at = order[j] + h;
                    keys[j] = at < n ? rank[at] : -1;
                }

                Array.Sort(keys, order, start, length);
                var first = start;
                for (var j = start + 1; j <= start + length; j++)
                {
                    if (j < start + length && keys[j] == keys[first])
                    {
                        continue;
                    }

                    for (var k = first; k < j; k++)
                    {
                        rank[order[k]] = j - 1;
                    }

                    if (j - first > 1)
                    {
                        tied.Add((first, j - first));
                    }

                    first = j;
                }
            }

            groups = tied;
        }

        return order;
    }
}
