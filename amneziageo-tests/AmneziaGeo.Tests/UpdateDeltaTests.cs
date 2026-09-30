using System.Buffers.Binary;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A delta makes the current version of a file from the earlier one byte for byte, stays small when the file changed
/// little, and a delta that does not hold together is refused.
/// </summary>
public sealed class UpdateDeltaTests
{
    [Fact]
    public void SmallEdits_MakeASmallDelta()
    {
        var earlier = Words(200_000, 1);
        var edited = new List<byte>(earlier);
        edited[1000] ^= 0x5a;
        edited[150_000] ^= 0x01;
        edited.RemoveRange(60_000, 500);
        edited.InsertRange(90_000, Words(1000, 2));
        edited.AddRange(earlier.AsSpan(10_000, 5000).ToArray());
        var current = edited.ToArray();

        var delta = UpdateDelta.Create(earlier, current);

        Assert.Equal(current, Apply(earlier, delta, current.Length));
        Assert.True(delta.Length < current.Length / 20, $"the delta takes {delta.Length} bytes");
    }

    [Fact]
    public void ShiftedCode_MakesASmallDelta()
    {
        // Code moved by a few bytes: every address inside it changes by the same amount.
        var earlier = Words(100_000, 3);
        var current = new byte[earlier.Length + 16];
        earlier.AsSpan(0, 50_000).CopyTo(current);
        earlier.AsSpan(50_000).CopyTo(current.AsSpan(50_016));
        for (var i = 50_016; i + 4 <= current.Length; i += 64)
        {
            BinaryPrimitives.WriteInt32LittleEndian(current.AsSpan(i), BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(i)) + 16);
        }

        var delta = UpdateDelta.Create(earlier, current);

        Assert.Equal(current, Apply(earlier, delta, current.Length));
        Assert.True(delta.Length < current.Length / 10, $"the delta takes {delta.Length} bytes");
    }

    [Fact]
    public void UnrelatedFiles_StillMakeTheCurrentOne()
    {
        var earlier = Noise(50_000, 4);
        var current = Noise(70_000, 5);

        Assert.Equal(current, Apply(earlier, UpdateDelta.Create(earlier, current), current.Length));
    }

    [Fact]
    public void RunsOfOneByte_MakeASmallDelta()
    {
        var earlier = new byte[300_000];
        var current = new byte[400_000];
        current[123_456] = 1;
        current[399_999] = 2;

        var delta = UpdateDelta.Create(earlier, current);

        Assert.Equal(current, Apply(earlier, delta, current.Length));
        Assert.True(delta.Length < 1000, $"the delta takes {delta.Length} bytes");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 3000)]
    [InlineData(3000, 0)]
    [InlineData(1, 1)]
    public void EmptyAndTinyFiles_Work(int earlierSize, int currentSize)
    {
        var earlier = Noise(earlierSize, 6);
        var current = Noise(currentSize, 7);

        Assert.Equal(current, Apply(earlier, UpdateDelta.Create(earlier, current), current.Length));
    }

    [Fact]
    public void ADeltaThatDoesNotHoldTogether_IsRefused()
    {
        var earlier = Words(20_000, 8);
        var current = Words(20_000, 9);
        var delta = UpdateDelta.Create(earlier, current);

        var magic = (byte[])delta.Clone();
        magic[0] ^= 1;
        var lengths = (byte[])delta.Clone();
        lengths[16]++;
        var sizes = (byte[])delta.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(sizes.AsSpan(8), current.Length + 1);

        Assert.Throws<InvalidDataException>(() => Apply(earlier, magic, current.Length));
        Assert.Throws<InvalidDataException>(() => Apply(earlier, lengths, current.Length));
        Assert.Throws<InvalidDataException>(() => Apply(earlier, sizes, current.Length));
        Assert.Throws<InvalidDataException>(() => Apply(earlier, delta[..^1], current.Length));
        Assert.Throws<InvalidDataException>(() => Apply(earlier, delta, current.Length + 1));
        Assert.Throws<InvalidDataException>(() => Apply(earlier, delta[..20], current.Length));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Suffixes_ComeOutSorted(int kind)
    {
        var data = kind switch
        {
            0 => Noise(5000, 12),
            1 => new byte[3000],
            2 => Enumerable.Range(0, 4000).Select(i => (byte)(i % 3)).ToArray(),
            3 => Words(6000, 13),
            _ => new byte[] { 42 },
        };

        var expected = Enumerable.Range(0, data.Length)
            .Order(Comparer<int>.Create((a, b) => data.AsSpan(a).SequenceCompareTo(data.AsSpan(b))))
            .ToArray();

        Assert.Equal(expected, UpdateDelta.Suffixes(data, CancellationToken.None));
    }

    private static byte[] Apply(byte[] earlier, byte[] delta, long size)
    {
        using var output = new MemoryStream();
        UpdateDelta.Apply(earlier, delta, output, size);
        return output.ToArray();
    }

    // Data with the repeats of code: a small vocabulary of words in a random order.
    private static byte[] Words(int size, int seed)
    {
        var random = new Random(seed);
        var words = Enumerable.Range(0, 64).Select(_ => Noise(random.Next(2, 12), random.Next())).ToArray();
        var data = new List<byte>(size);
        while (data.Count < size)
        {
            data.AddRange(words[random.Next(words.Length)]);
        }

        return data.Take(size).ToArray();
    }

    private static byte[] Noise(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }
}
