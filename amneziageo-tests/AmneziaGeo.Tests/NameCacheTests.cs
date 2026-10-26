using AmneziaGeo.Routing;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The addresses the names of a list came back with stay for the next connect of the same names; other names and a
/// damaged file find nothing.
/// </summary>
public sealed class NameCacheTests
{
    [Fact]
    public void TheAnswers_ComeBackForTheSameNames()
    {
        var key = NameCache.Key(["domain:youtube.com"], ["domain:ya.ru"], []);
        var text = NameCache.Format(key, new NameAnswers(["142.250.1.1/32", "142.250.1.2/32"], ["77.88.55.242/32"], []));

        var read = NameCache.Read(text, NameCache.Key(["domain:youtube.com"], ["domain:ya.ru"], []));

        Assert.NotNull(read);
        Assert.Equal(["142.250.1.1/32", "142.250.1.2/32"], read.Proxy);
        Assert.Equal(["77.88.55.242/32"], read.Direct);
        Assert.Empty(read.Block);
        Assert.Equal(3, read.Count);
    }

    [Fact]
    public void OtherNames_FindNothing()
    {
        var text = NameCache.Format(NameCache.Key(["domain:youtube.com"], [], []), new NameAnswers(["142.250.1.1/32"], [], []));

        Assert.Null(NameCache.Read(text, NameCache.Key(["domain:youtube.com", "domain:googlevideo.com"], [], [])));
    }

    [Fact]
    public void TheSameNameInAnotherBucket_FindsNothing()
    {
        var text = NameCache.Format(NameCache.Key(["domain:ya.ru"], [], []), new NameAnswers(["77.88.55.242/32"], [], []));

        Assert.Null(NameCache.Read(text, NameCache.Key([], ["domain:ya.ru"], [])));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("key X\nproxy\ndirect\n")]
    public void ADamagedFile_FindsNothing(string text)
    {
        Assert.Null(NameCache.Read(text, "X"));
    }

    [Fact]
    public void AnAddressThatIsNoRange_IsLeftOut()
    {
        var key = NameCache.Key(["domain:youtube.com"], [], []);
        var text = $"key {key}\nproxy 142.250.1.1/32 nonsense 10.0.0.1/33 2a00:1450::1/128\ndirect\nblock\n";

        var read = NameCache.Read(text, key);

        Assert.Equal(["142.250.1.1/32", "2a00:1450::1/128"], read!.Proxy);
    }
}
