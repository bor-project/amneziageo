using AmneziaGeo.Ipc;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The journal keeps a verdict as the English sentence it was rendered to, and the window names it again in the
/// reader's language by reading that sentence back. The two renderings have to stay a pair.
/// </summary>
public sealed class ProbePhraseTests
{
    [Fact]
    public void EveryVerdictReadsBackIntoWhatItWasMadeFrom()
    {
        (string Key, string[] Args)[] cases =
        [
            (ProbeVerdicts.Measured, ["12.3", "4.5", "speed.example"]),
            (ProbeVerdicts.Measured, ["12.3", "4.5"]),
            (ProbeVerdicts.NoRate, ["site.example"]),
            (ProbeVerdicts.Unreachable, ["10.0.0.1"]),
            (ProbeVerdicts.PathUnavailable, ["through"]),
            (ProbeVerdicts.NotConnected, []),
        ];

        foreach (var (key, args) in cases)
        {
            var (read, back) = ProbePhrase.Read(ProbePhrase.English(key, args));

            Assert.Equal(key, read);
            Assert.Equal(args, back);
        }
    }

    [Fact]
    public void ASentenceFromNowhereIsLeftAlone()
    {
        var (key, args) = ProbePhrase.Read("something the journal never wrote");

        Assert.Equal(string.Empty, key);
        Assert.Empty(args);
    }
}
