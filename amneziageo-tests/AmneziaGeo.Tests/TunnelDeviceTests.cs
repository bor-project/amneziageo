using System.Text.RegularExpressions;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The system name of a tunnel. The engine takes [A-Za-z0-9_=+.-] up to 32 characters and nothing else, and a
/// name it refuses ends the tunnel session in milliseconds without an error - so every configuration name a
/// person can type has to fold into one the engine takes, the same way in the agent and in the service.
/// </summary>
public sealed class TunnelDeviceTests
{
    private static readonly Regex _engineTakes = new("^[a-zA-Z0-9_=+.-]{1,32}$");

    [Theory]
    [InlineData("fi")]
    [InlineData("wintab_ADMINS")]
    [InlineData("home-adndroid-tv1-bor-sytes")]
    [InlineData("a.b+c=d_e-f")]
    public void NameOf_ANameTheEngineTakes_IsLeftAlone(string name)
    {
        Assert.Equal(name, TunnelDevice.NameOf(name));
    }

    [Theory]
    [InlineData("fi (2)")]
    [InlineData("bor-dev_fi (2)")]
    [InlineData("with space")]
    [InlineData("дом")]
    [InlineData("a/b\\c:d")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CON")]
    [InlineData("com1")]
    [InlineData("a-configuration-name-that-runs-well-past-the-thirty-two-character-limit")]
    public void NameOf_AnyOtherName_FoldsIntoOneTheEngineTakes(string name)
    {
        var device = TunnelDevice.NameOf(name);

        Assert.Matches(_engineTakes, device);
        Assert.True(TunnelDevice.IsAcceptable(device));
    }

    [Fact]
    public void NameOf_ACopyFromTheCatalog_KeepsTheReadablePart()
    {
        Assert.StartsWith("fi-2-", TunnelDevice.NameOf("fi (2)"), StringComparison.Ordinal);
    }

    [Fact]
    public void NameOf_NamesThatFoldAlike_StayApart()
    {
        var paren = TunnelDevice.NameOf("fi (2)");
        var spaced = TunnelDevice.NameOf("fi 2");
        var dashed = TunnelDevice.NameOf("fi-2");

        Assert.Equal("fi-2", dashed);
        Assert.NotEqual(paren, spaced);
        Assert.NotEqual(paren, dashed);
        Assert.NotEqual(spaced, dashed);
    }

    [Fact]
    public void NameOf_TheSameName_AnswersTheSameEveryTime()
    {
        Assert.Equal(TunnelDevice.NameOf("fi (2)"), TunnelDevice.NameOf("fi (2)"));
    }

    [Fact]
    public void IsAcceptable_ADeviceName_IsRefused()
    {
        Assert.False(TunnelDevice.IsAcceptable("CON"));
        Assert.False(TunnelDevice.IsAcceptable("lpt9"));
        Assert.NotEqual("CON", TunnelDevice.NameOf("CON"));
    }
}
