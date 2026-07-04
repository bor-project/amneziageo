using System.Text;

namespace AmneziaGeo.Geo;

/// <summary>
/// Minimal reader for the protobuf wire format.
/// </summary>
public ref struct ProtoReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    /// <summary>
    /// ctor
    /// </summary>
    public ProtoReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    /// <summary>
    /// True when the whole buffer has been consumed.
    /// </summary>
    public readonly bool End => _position >= _data.Length;

    /// <summary>
    /// Reads the next field tag as its number and wire type.
    /// </summary>
    public (int Field, int WireType) ReadTag()
    {
        var tag = ReadVarint();
        return ((int)(tag >> 3), (int)(tag & 0x7));
    }

    /// <summary>
    /// Reads a base-128 varint.
    /// </summary>
    public ulong ReadVarint()
    {
        ulong value = 0;
        var shift = 0;
        while (_position < _data.Length)
        {
            var b = _data[_position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return value;
    }

    /// <summary>
    /// Reads a length-delimited byte slice.
    /// </summary>
    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = (int)ReadVarint();
        if (length < 0 || length > _data.Length - _position)
        {
            throw new InvalidDataException("Malformed protobuf: length-delimited field runs past the buffer.");
        }

        var slice = _data.Slice(_position, length);
        _position += length;
        return slice;
    }

    /// <summary>
    /// Reads a length-delimited UTF-8 string.
    /// </summary>
    public string ReadString()
    {
        return Encoding.UTF8.GetString(ReadBytes());
    }

    /// <summary>
    /// Skips a field value of the given wire type.
    /// </summary>
    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint();
                break;
            case 1:
                _position += 8;
                break;
            case 2:
            {
                var length = (int)ReadVarint();
                _position = length < 0 || length > _data.Length - _position
                    ? _data.Length
                    : _position + length;
                break;
            }
            case 5:
                _position += 4;
                break;
            default:
                _position = _data.Length;
                break;
        }
    }
}
