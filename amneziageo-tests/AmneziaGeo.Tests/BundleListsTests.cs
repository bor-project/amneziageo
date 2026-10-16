using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Routing lists in a bundle go by their ids: an export picks them by number or by name, an import finds the held
/// list by the lasting id the block carries.
/// </summary>
public sealed class BundleListsTests
{
    private static readonly IReadOnlyList<RoutingList> Lists =
    [
        List(3, "work", "uid-3"),
        List(5, "work", "uid-5"),
        List(7, "home", "uid-7"),
    ];

    [Fact]
    public void ANumber_PicksThatListAlone()
    {
        var (picked, missing) = BundleLists.Pick(Lists, ["5"]);

        Assert.Equal([5L], picked.Select(one => one.List.Id));
        Assert.Equal("5", Assert.Single(picked).Key);
        Assert.Empty(missing);
    }

    [Fact]
    public void AName_PicksEveryListUnderIt()
    {
        var (picked, _) = BundleLists.Pick(Lists, ["work"]);

        Assert.Equal([3L, 5L], picked.Select(one => one.List.Id));
    }

    [Fact]
    public void AListPickedTwice_GoesOnce_AndAWordThatPicksNothingComesBack()
    {
        var (picked, missing) = BundleLists.Pick(Lists, ["3", "work", "nowhere", "99"]);

        Assert.Equal([3L, 5L], picked.Select(one => one.List.Id));
        Assert.Equal(["nowhere", "99"], missing);
    }

    [Fact]
    public void ABlock_IsTheListHeldUnderItsId()
    {
        Assert.Equal(5, BundleLists.Held(Block("other name", "uid-5"), Lists)!.Id);
    }

    [Fact]
    public void ABlockWithoutAnIdOrWithAnUnknownOne_IsAListOfItsOwn()
    {
        Assert.Null(BundleLists.Held(Block("work", null), Lists));
        Assert.Null(BundleLists.Held(Block("work", "uid-9"), Lists));
    }

    [Fact]
    public void ABlockWithoutAName_LandsUnderTheFallback()
    {
        Assert.Equal(BundleLists.FallbackName, BundleLists.NameOf(Block("  ", null)));
        Assert.Equal("work", BundleLists.NameOf(Block(" work ", null)));
    }

    [Fact]
    public void TheIdOfAList_TravelsInTheBundle()
    {
        var bundle = new PortableBundle.Bundle(
            PortableBundle.FormatTag,
            PortableBundle.CurrentVersion,
            [],
            [Block("work", "uid-5")],
            ActiveRoutingListId: "uid-5");

        var back = PortableBundle.Deserialize(PortableBundle.Serialize(bundle))!;

        Assert.Equal("uid-5", Assert.Single(back.RoutingLists).Id);
        Assert.Equal("uid-5", back.ActiveRoutingListId);
    }

    [Fact]
    public void ABundleOfAnEarlierBuild_ReadsWithoutIds()
    {
        var back = PortableBundle.Deserialize("""
            {"format":"amneziageo-bundle","version":2,"configs":[],"routingLists":[{"name":"work","rules":["proxy|domain:a.example"]}]}
            """)!;

        Assert.Null(Assert.Single(back.RoutingLists).Id);
        Assert.Null(BundleLists.Held(back.RoutingLists[0], Lists));
    }

    private static RoutingList List(long id, string name, string uid) =>
        new(id, name, [], [], [], [], [], [], [], [], [], uid);

    private static PortableBundle.RoutingBlock Block(string name, string? id) =>
        new(name, ["proxy|domain:a.example"], null, id);
}
