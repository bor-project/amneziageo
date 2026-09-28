using AmneziaGeo.Ui.Services;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class HandScaleTests
{
    [Theory]
    [InlineData(1.0, 1.3)]
    [InlineData(0.85, 1.105)]
    [InlineData(1.15, 1.495)]
    [InlineData(1.3, 1.495)]
    [InlineData(2.0, 1.495)]
    public void ThePhoneScale_FollowsTheSystemFontUpToTheCap(double fontScale, double expected)
    {
        Assert.Equal(expected, UiPlatform.WithFontScale(1.3, fontScale), 6);
    }

    [Fact]
    public void AnUnknownFontScale_KeepsTheScale()
    {
        Assert.Equal(1.15, UiPlatform.WithFontScale(1.15, 0), 6);
    }
}
