using System.Text;
using AmneziaGeo.Decl;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The list of deltas beside a release asset is read only when every line holds together and the deltas cover their
/// pack one after another.
/// </summary>
public sealed class UpdateDeltaListTests
{
    private static readonly string A = new('a', 64);
    private static readonly string B = new('b', 64);
    private static readonly string C = new('c', 64);

    [Fact]
    public void AList_NamesEveryDelta()
    {
        var entries = UpdateDeltaList.Parse(Text($"{A} {B} 5000 0 100", $"{A} {C} 6000 100 50"), 150);

        Assert.Equal(
            new[] { new UpdateDeltaEntry(A, B, 5000, 0, 100), new UpdateDeltaEntry(A, C, 6000, 100, 50) },
            entries);
    }

    [Theory]
    [InlineData("# amneziageo deltas 2", "aaaa bbbb 5000 0 150")]
    [InlineData(UpdateDeltaList.Head, "aaaa bbbb 5000 0 100 extra")]
    [InlineData(UpdateDeltaList.Head, "AAAA bbbb 5000 0 150")]
    [InlineData(UpdateDeltaList.Head, "aaaa aaaa 5000 0 150")]
    [InlineData(UpdateDeltaList.Head, "aaaa bbbb 5000 10 140")]
    [InlineData(UpdateDeltaList.Head, "aaaa bbbb 5000 0 100")]
    [InlineData(UpdateDeltaList.Head, "aaaa bbbb 5000 0 0")]
    [InlineData(UpdateDeltaList.Head, "aaaa bbbb -5 0 150")]
    [InlineData(UpdateDeltaList.Head, "aaaa bbbb 999999999999 0 150")]
    [InlineData(UpdateDeltaList.Head, "aaaa bbbb 5000 0 75\naaaa bbbb 5000 75 75")]
    [InlineData(UpdateDeltaList.Head, "")]
    public void AListThatDoesNotHoldTogether_IsRefused(string head, string lines)
    {
        var text = head + "\n" + lines.Replace("aaaa", A, StringComparison.Ordinal).Replace("bbbb", B, StringComparison.Ordinal)
            .Replace("AAAA", A.ToUpperInvariant(), StringComparison.Ordinal) + "\n";

        Assert.Throws<InvalidDataException>(() => UpdateDeltaList.Parse(Encoding.UTF8.GetBytes(text), 150));
    }

    private static byte[] Text(params string[] lines) =>
        Encoding.UTF8.GetBytes(UpdateDeltaList.Head + "\n" + string.Join('\n', lines) + "\n");
}
