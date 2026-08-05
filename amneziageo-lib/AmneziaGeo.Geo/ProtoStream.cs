using System.Buffers;

namespace AmneziaGeo.Geo;

/// <summary>
/// Reads the protobuf wire format off a seekable stream, so a database is scanned one entry at a time instead of
/// being held whole.
/// </summary>
internal sealed class ProtoStream(Stream stream)
{
    /// <summary>
    /// Current offset.
    /// </summary>
    public long Position => stream.Position;

    /// <summary>
    /// Moves to an absolute offset.
    /// </summary>
    public void Seek(long position)
    {
        stream.Seek(position, SeekOrigin.Begin);
    }

    /// <summary>
    /// Reads the next field tag as its number and wire type; false at the end of the stream.
    /// </summary>
    public bool TryReadTag(out int field, out int wireType)
    {
        field = 0;
        wireType = 0;
        if (!TryReadVarint(out var tag))
        {
            return false;
        }

        field = (int)(tag >> 3);
        wireType = (int)(tag & 0x7);
        return true;
    }

    /// <summary>
    /// Reads the length of a length-delimited field.
    /// </summary>
    public int ReadLength()
    {
        if (!TryReadVarint(out var value) || value > int.MaxValue)
        {
            throw new InvalidDataException("Malformed protobuf: length-delimited field has no usable length.");
        }

        return (int)value;
    }

    /// <summary>
    /// Reads a field body into a buffer taken from the pool; the caller returns it.
    /// </summary>
    public byte[] Rent(int length)
    {
        if (length < 0 || (stream.CanSeek && length > stream.Length - stream.Position))
        {
            throw new InvalidDataException("Malformed protobuf: length-delimited field runs past the file.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            stream.ReadExactly(buffer, 0, length);
            return buffer;
        }
        catch (Exception)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>
    /// Gives a rented buffer back to the pool.
    /// </summary>
    public static void Return(byte[] buffer)
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }

    /// <summary>
    /// Steps over a field value of the given wire type.
    /// </summary>
    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                TryReadVarint(out _);
                break;
            case 1:
                Advance(8);
                break;
            case 2:
                Advance(ReadLength());
                break;
            case 5:
                Advance(4);
                break;
            default:
                throw new InvalidDataException($"Malformed protobuf: unknown wire type {wireType}.");
        }
    }

    /// <summary>
    /// Steps forward over the given number of bytes.
    /// </summary>
    public void Advance(long count)
    {
        if (count <= 0)
        {
            return;
        }

        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(count, buffer.Length));
                if (read <= 0)
                {
                    throw new EndOfStreamException();
                }

                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool TryReadVarint(out ulong value)
    {
        value = 0;
        var shift = 0;
        while (shift <= 63)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                if (shift > 0)
                {
                    throw new EndOfStreamException();
                }

                return false;
            }

            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        throw new InvalidDataException("Malformed protobuf: varint runs past 10 bytes.");
    }
}
