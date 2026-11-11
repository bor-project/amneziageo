using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class RoutingPathSuggestionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"routing-path-{Guid.NewGuid():N}");

    /// <summary>
    /// ctor
    /// </summary>
    public RoutingPathSuggestionsTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData("future.exe", "path")]
    [InlineData("future.txt", "path")]
    [InlineData("future", "dir")]
    public void MissingPath_RemainsPickable(string name, string kind)
    {
        var path = Path.Combine(_root, name);

        var match = Assert.Single(RoutingPathSuggestions.Find(path));

        Assert.Equal(AppPathToken.NormalizeAppRule($"app:{kind}={path}"), match.Token);
        Assert.True(match.Missing);
    }

    [Fact]
    public void ExistingNonExecutableFile_RemainsPickable()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "{}");

        var match = Assert.Single(RoutingPathSuggestions.Find('"' + path + '"'));

        Assert.Equal(AppPathToken.NormalizeAppRule("app:path=" + path), match.Token);
        Assert.False(match.Missing);
    }

    [Fact]
    public void PathPrefix_OffersExecutablesBeforeFolders()
    {
        var exe = Path.Combine(_root, "sample.exe");
        File.WriteAllText(exe, string.Empty);
        Directory.CreateDirectory(Path.Combine(_root, "sample-folder"));

        var matches = RoutingPathSuggestions.Find(Path.Combine(_root, "sample"));

        Assert.Equal(AppPathToken.NormalizeAppRule("app:path=" + exe), matches[0].Token);
        Assert.Contains(matches, match => match.Token.Contains("sample-folder", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingFile_IsFoundAfterItAppears()
    {
        var path = Path.Combine(_root, "later.exe");
        Assert.True(Assert.Single(RoutingPathSuggestions.Find(path)).Missing);
        File.WriteAllText(path, string.Empty);

        Assert.False(Assert.Single(RoutingPathSuggestions.Find(path)).Missing);
    }

    [Fact]
    public void FolderSeparator_DoesNotDuplicateTheFolder()
    {
        var matches = RoutingPathSuggestions.Find(_root + Path.DirectorySeparatorChar);

        var match = Assert.Single(matches);
        Assert.Equal(AppPathToken.NormalizeAppRule("app:dir=" + _root), match.Token);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root))
        {
            File.Delete(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            Directory.Delete(directory);
        }

        Directory.Delete(_root);
    }
}
