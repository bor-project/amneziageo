using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A window on the screen keeps its frames for a moment after it changed, and gives them up once nothing on it
/// changes.
/// </summary>
public sealed class FramePaceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(149)]
    public void RightAfterAChange_TheFramesGoOn(long sinceTheChange)
    {
        Assert.False(FramePace.Rests(100_000 + sinceTheChange, 100_000));
    }

    [Theory]
    [InlineData(150)]
    [InlineData(5_000)]
    public void OnceNothingChanges_TheFramesStop(long sinceTheChange)
    {
        Assert.True(FramePace.Rests(100_000 + sinceTheChange, 100_000));
    }

    [Fact]
    public void AWindowAtRest_DrawsOnceASecond()
    {
        Assert.Equal(1_000, FramePace.BeatMs);
    }
}
