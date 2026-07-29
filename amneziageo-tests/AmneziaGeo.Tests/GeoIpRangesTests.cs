using System.Net;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The range set replaces materialized geoip routes: a country rule is answered by search instead of thousands of
/// route-table entries. Merging must not change what is covered, in either direction.
/// </summary>
public sealed class GeoIpRangesTests
{
    private static uint Num(string address)
    {
        Assert.True(GeoIpRanges.TryToNumeric(IPAddress.Parse(address), out var value));
        return value;
    }

    [Fact]
    public void Prefix_CoversItsOwnRange()
    {
        var ranges = GeoIpRanges.Build(["77.88.32.0/19"]);

        Assert.True(ranges.Contains(Num("77.88.55.242")));
        Assert.True(ranges.Contains(Num("77.88.32.0")));
        Assert.True(ranges.Contains(Num("77.88.63.255")));
        Assert.False(ranges.Contains(Num("77.88.64.0")));
        Assert.False(ranges.Contains(Num("77.88.31.255")));
    }

    [Fact]
    public void AdjacentPrefixes_MergeIntoOneRange()
    {
        var ranges = GeoIpRanges.Build(["10.0.0.0/24", "10.0.1.0/24", "10.0.2.0/24"]);

        Assert.Equal(1, ranges.Count);
        Assert.True(ranges.Contains(Num("10.0.1.7")));
        Assert.True(ranges.Contains(Num("10.0.2.255")));
        Assert.False(ranges.Contains(Num("10.0.3.0")));
    }

    [Fact]
    public void OverlappingPrefixes_MergeAndKeepTheWidestEnd()
    {
        var ranges = GeoIpRanges.Build(["10.0.0.0/16", "10.0.5.0/24"]);

        Assert.Equal(1, ranges.Count);
        Assert.True(ranges.Contains(Num("10.0.255.255")));
        Assert.False(ranges.Contains(Num("10.1.0.0")));
    }

    [Fact]
    public void GapBetweenPrefixes_IsNotCovered()
    {
        var ranges = GeoIpRanges.Build(["10.0.0.0/24", "10.0.2.0/24"]);

        Assert.Equal(2, ranges.Count);
        Assert.True(ranges.Contains(Num("10.0.0.1")));
        Assert.False(ranges.Contains(Num("10.0.1.5")));
        Assert.True(ranges.Contains(Num("10.0.2.1")));
    }

    [Fact]
    public void BareAddress_IsASingleHostRange()
    {
        var ranges = GeoIpRanges.Build(["8.8.8.8"]);

        Assert.True(ranges.Contains(Num("8.8.8.8")));
        Assert.False(ranges.Contains(Num("8.8.8.9")));
    }

    [Fact]
    public void NonIpv4Entries_AreSkipped()
    {
        var ranges = GeoIpRanges.Build(["2001:470::/29", "not-a-cidr", "10.0.0.0/8", "10.0.0.0/99"]);

        Assert.Equal(1, ranges.Count);
        Assert.True(ranges.Contains(Num("10.1.2.3")));
    }

    [Fact]
    public void DefaultRoute_CoversEverything()
    {
        var ranges = GeoIpRanges.Build(["0.0.0.0/0"]);

        Assert.True(ranges.Contains(Num("0.0.0.0")));
        Assert.True(ranges.Contains(Num("255.255.255.255")));
    }

    [Fact]
    public void TopOfSpace_DoesNotOverflowTheMerge()
    {
        var ranges = GeoIpRanges.Build(["255.255.255.254/31"]);

        Assert.True(ranges.Contains(Num("255.255.255.255")));
        Assert.False(ranges.Contains(Num("255.255.255.253")));
    }

    [Fact]
    public void EmptyInput_MatchesNothing()
    {
        var ranges = GeoIpRanges.Build([]);

        Assert.Equal(0, ranges.Count);
        Assert.False(ranges.Contains(Num("1.2.3.4")));
    }

    [Fact]
    public void MergedSet_CoversExactlyTheSameAddressesAsTheInput()
    {
        string[] input =
        [
            "5.8.16.0/23", "5.8.18.0/24", "5.8.20.0/22", "2.60.0.0/15", "2.62.0.0/16",
            "77.88.32.0/19", "87.240.128.0/18", "90.156.232.0/21", "5.255.255.242/32",
        ];
        var ranges = GeoIpRanges.Build(input);

        // Every address of every input prefix is still covered.
        foreach (var cidr in input)
        {
            var slash = cidr.IndexOf('/');
            var network = Num(cidr[..slash]);
            var bits = byte.Parse(cidr[(slash + 1)..]);
            var size = bits == 32 ? 1u : 1u << (32 - bits);
            Assert.True(ranges.Contains(network));
            Assert.True(ranges.Contains(network + size - 1));
        }

        // And nothing outside them became covered.
        Assert.False(ranges.Contains(Num("8.8.8.8")));
        Assert.False(ranges.Contains(Num("2.59.255.255")));
        Assert.False(ranges.Contains(Num("2.63.0.0")));
        Assert.False(ranges.Contains(Num("77.88.64.0")));
    }
}
