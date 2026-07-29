using System.Net;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Aggregation of the bypass list: a geoip country rule expands into thousands of adjacent prefixes, each of
/// which otherwise costs a route and a pair of WFP filters. Folding them must not change what is covered.
/// </summary>
public sealed class CidrAggregatorTests
{
    [Fact]
    public void AlignedSiblings_FoldIntoTheParent()
    {
        var result = CidrAggregator.Aggregate(["10.0.0.0/25", "10.0.0.128/25"]);

        Assert.Equal(["10.0.0.0/24"], result);
    }

    [Fact]
    public void NestedPrefix_IsAbsorbed()
    {
        var result = CidrAggregator.Aggregate(["10.0.0.0/24", "10.0.0.128/25", "10.0.0.0/24"]);

        Assert.Equal(["10.0.0.0/24"], result);
    }

    [Fact]
    public void FullRun_CollapsesAllTheWayUp()
    {
        var result = CidrAggregator.Aggregate(["10.0.0.0/24", "10.0.1.0/24", "10.0.2.0/24", "10.0.3.0/24"]);

        Assert.Equal(["10.0.0.0/22"], result);
    }

    [Fact]
    public void UnalignedNeighbours_StayApart()
    {
        var result = CidrAggregator.Aggregate(["10.0.1.0/24", "10.0.2.0/24"]);

        Assert.Equal(["10.0.1.0/24", "10.0.2.0/24"], result);
    }

    [Fact]
    public void IncompletePair_StaysApart()
    {
        // 10.0.0.0/24 + 10.0.1.0/25 is not a whole /23, so widening would cover addresses nobody listed.
        var result = CidrAggregator.Aggregate(["10.0.0.0/24", "10.0.1.0/25"]);

        Assert.Equal(["10.0.0.0/24", "10.0.1.0/25"], result);
    }

    [Fact]
    public void DefaultRoute_SwallowsEverything()
    {
        var result = CidrAggregator.Aggregate(["0.0.0.0/0", "10.0.0.0/8", "192.168.1.0/24"]);

        Assert.Equal(["0.0.0.0/0"], result);
    }

    [Fact]
    public void NonIpv4Entries_PassThrough()
    {
        var result = CidrAggregator.Aggregate(["fc00::/7", "10.0.0.0/8", "not-a-cidr"]);

        Assert.Contains("fc00::/7", result);
        Assert.Contains("not-a-cidr", result);
        Assert.Contains("10.0.0.0/8", result);
    }

    [Fact]
    public void HostRoutes_FoldIntoASingleBlock()
    {
        var hosts = new List<string>();
        for (var i = 0; i < 256; i++)
        {
            hosts.Add($"77.88.44.{i}/32");
        }

        Assert.Equal(["77.88.44.0/24"], CidrAggregator.Aggregate(hosts));
    }

    [Fact]
    public void CoverageIsPreserved_OnAScatteredSet()
    {
        var input = new List<string>
        {
            "2.16.20.0/23", "2.16.22.0/23", "2.16.53.0/24", "2.17.144.0/23", "2.17.146.0/24",
            "77.88.44.0/24", "77.88.45.0/24", "77.88.46.0/23", "87.240.128.0/18", "90.156.232.0/21",
            "192.168.0.0/16", "10.0.0.0/8", "172.16.0.0/12",
        };

        var result = CidrAggregator.Aggregate(input);

        Assert.True(result.Count < input.Count);
        foreach (var probe in Probes(input))
        {
            Assert.True(Covered(result, probe), $"{probe} dropped out of the aggregated set");
        }

        // Nothing outside the input may become covered.
        foreach (var outside in new[] { "2.16.19.255", "2.16.24.0", "77.88.48.0", "90.156.240.0", "8.8.8.8" })
        {
            var address = IPAddress.Parse(outside);
            Assert.Equal(Covered(input, address), Covered(result, address));
        }
    }

    private static IEnumerable<IPAddress> Probes(IEnumerable<string> cidrs)
    {
        foreach (var cidr in cidrs)
        {
            var slash = cidr.IndexOf('/');
            var network = ToUint(IPAddress.Parse(cidr[..slash]));
            var prefix = byte.Parse(cidr[(slash + 1)..]);
            var size = prefix == 0 ? uint.MaxValue : (1u << (32 - prefix)) - 1;

            yield return ToAddress(network);
            yield return ToAddress(network + size);
            yield return ToAddress(network + (size / 2));
        }
    }

    private static bool Covered(IEnumerable<string> cidrs, IPAddress address)
    {
        var value = ToUint(address);
        foreach (var cidr in cidrs)
        {
            var slash = cidr.IndexOf('/');
            if (slash < 0 || !IPAddress.TryParse(cidr[..slash], out var network) || !byte.TryParse(cidr[(slash + 1)..], out var prefix))
            {
                continue;
            }

            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            if ((value & mask) == (ToUint(network) & mask))
            {
                return true;
            }
        }

        return false;
    }

    private static uint ToUint(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress ToAddress(uint value)
    {
        return new IPAddress(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
    }
}
