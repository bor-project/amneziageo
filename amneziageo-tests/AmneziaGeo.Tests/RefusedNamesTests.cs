using AmneziaGeo.Routing;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The names the resolver refused under a block rule, which the sessions summary counts as blocked while no
/// address of such a name is ever held.
/// </summary>
public sealed class RefusedNamesTests
{
    [Fact]
    public void ARefusedName_ComesBackWithTheTimeSinceItWasAsked()
    {
        var now = 10_000L;
        var refused = new RefusedNames(now: () => now);

        refused.Note("Ads.Example.");
        now += 4_000;
        var fresh = refused.Fresh(60);

        Assert.Single(fresh);
        Assert.Equal("ads.example", fresh[0].Name);
        Assert.Equal(4, fresh[0].IdleSeconds);
    }

    [Fact]
    public void ANameRefusedAgain_StaysOneRowAndReadsAsFresh()
    {
        var now = 10_000L;
        var refused = new RefusedNames(now: () => now);

        refused.Note("ads.example");
        now += 30_000;
        refused.Note("ads.example");
        var fresh = refused.Fresh(60);

        Assert.Single(fresh);
        Assert.Equal(0, fresh[0].IdleSeconds);
    }

    [Fact]
    public void ANameOutsideTheWindow_IsGone()
    {
        var now = 10_000L;
        var refused = new RefusedNames(now: () => now);

        refused.Note("ads.example");
        now += 61_000;

        Assert.Empty(refused.Fresh(60));
        Assert.Equal(0, refused.Count);
    }

    [Fact]
    public void PastTheRoomItHolds_TheOldestNameGoes()
    {
        var now = 10_000L;
        var refused = new RefusedNames(2, () => now);

        refused.Note("one.example");
        now += 1_000;
        refused.Note("two.example");
        now += 1_000;
        refused.Note("three.example");
        var names = new List<string>();
        foreach (var one in refused.Fresh(600))
        {
            names.Add(one.Name);
        }

        Assert.Equal(2, names.Count);
        Assert.DoesNotContain("one.example", names);
        Assert.Contains("three.example", names);
    }
}
