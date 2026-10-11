using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The programs a list keeps direct are refused the tunnel adapter by their files, so the rules have to name the
/// files the disk holds.
/// </summary>
public sealed class AppMatcherImagesTests
{
    [Fact]
    public void Images_NameTheFileOfAPathRuleAndTheProgramsUnderAFolderRule()
    {
        var root = Directory.CreateTempSubdirectory("ageo-images-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "tool", "bin"));
            var named = Path.Combine(root, "bank.exe");
            var nested = Path.Combine(root, "tool", "bin", "helper.exe");
            File.WriteAllText(named, string.Empty);
            File.WriteAllText(nested, string.Empty);
            File.WriteAllText(Path.Combine(root, "tool", "readme.txt"), string.Empty);
            var matcher = new AppMatcher(["path=" + named, "path=" + Path.Combine(root, "gone.exe"), "dir=" + Path.Combine(root, "tool")],
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, past: true);

            var images = matcher.Images(8);

            Assert.Equal(new[] { named, nested }, images);
            Assert.Single(matcher.Images(1));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

/// <summary>
/// The rules by application are replaced on a running matcher, so a list edited while the tunnel stands is in
/// force without reconnecting it.
/// </summary>
public class AppMatcherReloadTests
{
    private static AppMatcher Matcher(params string[] rules) => new(rules, NullLogger<AppMatcher>.Instance);

    [Fact]
    public void AnEditedRuleSetIsPutInForce()
    {
        var matcher = Matcher("name=first.exe");

        Assert.True(matcher.Reload(["name=first.exe", "pkg=Some.App_publisher"]));
        Assert.True(matcher.HasMatchers);
    }

    [Fact]
    public void TheSameRulesAgainChangeNothing()
    {
        var matcher = Matcher("name=first.exe");

        Assert.False(matcher.Reload(["NAME=first.exe".ToLowerInvariant()]));
    }

    [Fact]
    public void RemovingEveryRuleLeavesNothingMatched()
    {
        var matcher = Matcher("name=first.exe");

        Assert.True(matcher.Reload([]));
        Assert.False(matcher.HasMatchers);
    }
}
