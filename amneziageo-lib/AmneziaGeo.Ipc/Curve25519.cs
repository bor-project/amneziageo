namespace AmneziaGeo.Ipc;

/// <summary>
/// The keys of AmneziaWG, made on curve25519: the public key that belongs to a private one, and the secret two
/// sides reach without either key travelling.
/// </summary>
public static class Curve25519
{
    /// <summary>
    /// The length of a key in bytes.
    /// </summary>
    public const int KeySize = 32;

    /// <summary>
    /// The length of a key written in base64.
    /// </summary>
    public const int TextSize = 44;

    private const int Limbs = 16;

    private static readonly long[] Factor = Number(0xDB41, 1);

    /// <summary>
    /// Returns the public key that belongs to a private key.
    /// </summary>
    public static byte[] PublicOf(ReadOnlySpan<byte> privateKey)
    {
        var point = new byte[KeySize];
        point[0] = 9;

        return Product(privateKey, point);
    }

    /// <summary>
    /// Returns the public key that belongs to a private key written in base64.
    /// </summary>
    public static string PublicOf(string privateKey) =>
        Convert.ToBase64String(PublicOf(Bytes(privateKey)));

    /// <summary>
    /// Returns the secret two sides reach from a private key and the public key of the other side.
    /// </summary>
    public static byte[] Product(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> point)
    {
        if (scalar.Length != KeySize || point.Length != KeySize)
        {
            throw new ArgumentException($"a key is {KeySize} bytes long");
        }

        return Ladder(scalar, point);
    }

    /// <summary>
    /// Tells whether a text is a key of the right length in base64.
    /// </summary>
    public static bool IsKey(string? text) =>
        text is { Length: TextSize } && Convert.TryFromBase64String(text, new byte[KeySize], out var written) && written == KeySize;

    /// <summary>
    /// Reads a key written in base64.
    /// </summary>
    public static byte[] Bytes(string text) =>
        IsKey(text) ? Convert.FromBase64String(text) : throw new FormatException("the text is not a key in base64");

    private static void Clamp(byte[] scalar)
    {
        scalar[0] &= 248;
        scalar[31] = (byte)((scalar[31] & 127) | 64);
    }

    private static byte[] Ladder(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> point)
    {
        var z = scalar.ToArray();
        Clamp(z);

        var x = Unpack(point);
        var a = Number(1);
        var b = (long[])x.Clone();
        var c = Number();
        var d = Number(1);
        var e = Number();
        var f = Number();

        for (var i = 254; i >= 0; i--)
        {
            var bit = (long)((z[i >> 3] >> (i & 7)) & 1);
            Swap(a, b, bit);
            Swap(c, d, bit);
            Add(e, a, c);
            Subtract(a, a, c);
            Add(c, b, d);
            Subtract(b, b, d);
            Multiply(d, e, e);
            Multiply(f, a, a);
            Multiply(a, c, a);
            Multiply(c, b, e);
            Add(e, a, c);
            Subtract(a, a, c);
            Multiply(b, a, a);
            Subtract(c, d, f);
            Multiply(a, c, Factor);
            Add(a, a, d);
            Multiply(c, c, a);
            Multiply(a, d, f);
            Multiply(d, b, x);
            Multiply(b, e, e);
            Swap(a, b, bit);
            Swap(c, d, bit);
        }

        Multiply(a, a, Invert(c));

        return Pack(a);
    }

    private static long[] Number(params long[] head)
    {
        var value = new long[Limbs];
        head.CopyTo(value, 0);

        return value;
    }

    private static long[] Unpack(ReadOnlySpan<byte> bytes)
    {
        var value = new long[Limbs];
        for (var i = 0; i < Limbs; i++)
        {
            value[i] = bytes[2 * i] + ((long)bytes[(2 * i) + 1] << 8);
        }

        value[15] &= 0x7fff;

        return value;
    }

    private static byte[] Pack(long[] value)
    {
        var head = (long[])value.Clone();
        Carry(head);
        Carry(head);
        Carry(head);

        var trimmed = new long[Limbs];
        for (var round = 0; round < 2; round++)
        {
            trimmed[0] = head[0] - 0xffed;
            for (var i = 1; i < 15; i++)
            {
                trimmed[i] = head[i] - 0xffff - ((trimmed[i - 1] >> 16) & 1);
                trimmed[i - 1] &= 0xffff;
            }

            trimmed[15] = head[15] - 0x7fff - ((trimmed[14] >> 16) & 1);
            var carried = (trimmed[15] >> 16) & 1;
            trimmed[14] &= 0xffff;
            Swap(head, trimmed, 1 - carried);
        }

        var bytes = new byte[KeySize];
        for (var i = 0; i < Limbs; i++)
        {
            bytes[2 * i] = (byte)(head[i] & 0xff);
            bytes[(2 * i) + 1] = (byte)((head[i] >> 8) & 0xff);
        }

        return bytes;
    }

    private static void Add(long[] result, long[] left, long[] right)
    {
        for (var i = 0; i < Limbs; i++)
        {
            result[i] = left[i] + right[i];
        }
    }

    private static void Subtract(long[] result, long[] left, long[] right)
    {
        for (var i = 0; i < Limbs; i++)
        {
            result[i] = left[i] - right[i];
        }
    }

    private static void Multiply(long[] result, long[] left, long[] right)
    {
        var wide = new long[(2 * Limbs) - 1];
        for (var i = 0; i < Limbs; i++)
        {
            for (var j = 0; j < Limbs; j++)
            {
                wide[i + j] += left[i] * right[j];
            }
        }

        for (var i = 0; i < Limbs - 1; i++)
        {
            wide[i] += 38 * wide[i + Limbs];
        }

        Array.Copy(wide, result, Limbs);
        Carry(result);
        Carry(result);
    }

    private static void Carry(long[] value)
    {
        for (var i = 0; i < Limbs; i++)
        {
            value[i] += 1L << 16;
            var carried = value[i] >> 16;
            value[(i + 1) * (i < 15 ? 1 : 0)] += carried - 1 + (37 * (carried - 1) * (i == 15 ? 1 : 0));
            value[i] -= carried << 16;
        }
    }

    private static void Swap(long[] left, long[] right, long bit)
    {
        var mask = ~(bit - 1);
        for (var i = 0; i < Limbs; i++)
        {
            var moved = mask & (left[i] ^ right[i]);
            left[i] ^= moved;
            right[i] ^= moved;
        }
    }

    private static long[] Invert(long[] value)
    {
        var power = (long[])value.Clone();
        for (var step = 253; step >= 0; step--)
        {
            Multiply(power, power, power);
            if (step != 2 && step != 4)
            {
                Multiply(power, power, value);
            }
        }

        return power;
    }
}
