using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The DNS-redirect state file: what a restore is told to put back. An adapter is recorded by its GUID, and
/// an adapter that took its servers from DHCP is recorded as taking them from DHCP - a file that says neither
/// is what put one network's resolver on another adapter, statically, for good.
/// </summary>
public sealed class DnsStateFileTests
{
    private const string Guid1 = "{11112222-3333-4444-5555-666677778888}";
    private const string Guid2 = "{99990000-1111-2222-3333-444455556666}";

    [Fact]
    public void Format_RoundTrips_ServersAndTargets()
    {
        var saved = new Dictionary<string, SavedDns>
        {
            [Guid1] = new(["192.168.1.1", "8.8.8.8"], ["2001:4860:4860::8888"]),
            [Guid2] = new([], []),
        };

        var state = DnsStateFile.Parse(DnsStateFile.Format(saved, ["127.0.0.1"]));

        Assert.Equal(["127.0.0.1"], state.RedirectTargets);
        var first = Single(state, Guid1);
        Assert.Equal(["192.168.1.1", "8.8.8.8"], first.Saved.V4);
        Assert.Equal(["2001:4860:4860::8888"], first.Saved.V6);
        Assert.False(first.Legacy);

        var second = Single(state, Guid2);
        Assert.Empty(second.Saved.V4);
        Assert.Empty(second.Saved.V6);
    }

    [Fact]
    public void Parse_AdapterOnDhcp_RecordedAsAutomatic()
    {
        // Empty means automatic: a restore hands the adapter back to DHCP instead of pinning it to whatever
        // address the lease happened to carry.
        var state = DnsStateFile.Parse(["@redirect=127.0.0.1", $"{Guid1}=|"]);

        var entry = Single(state, Guid1);
        Assert.Empty(entry.Saved.V4);
        Assert.Empty(entry.Saved.V6);
        Assert.False(entry.Legacy);
    }

    [Fact]
    public void Parse_KeepsAdapterIdentity_NotTheInterfaceIndex()
    {
        var state = DnsStateFile.Parse([$"{Guid1}=192.168.1.1|"]);

        var entry = Single(state, Guid1);
        Assert.Equal(Guid1, entry.Guid);
        Assert.Null(entry.Index);
    }

    [Fact]
    public void Parse_IndexKeyedFile_IsLegacy()
    {
        // Written by a build that keyed state by interface index; Windows hands that index to another adapter.
        var state = DnsStateFile.Parse(["@redirect=127.0.0.1", "12=192.168.1.1,1.1.1.1"]);

        var entry = Assert.Single(state.Entries);
        Assert.True(entry.Legacy);
        Assert.Null(entry.Guid);
        Assert.Equal(12u, entry.Index);
        Assert.Equal(["192.168.1.1", "1.1.1.1"], entry.Saved.V4);
    }

    [Fact]
    public void Parse_NoHeader_HasNoTargets()
    {
        var state = DnsStateFile.Parse(["12=192.168.1.1"]);

        Assert.Empty(state.RedirectTargets);
    }

    [Fact]
    public void Parse_SkipsUnreadableLines()
    {
        var state = DnsStateFile.Parse(["", "not a line", "@redirect=127.0.0.1", $"{Guid1}=|::1"]);

        Assert.Single(state.Entries);
        Assert.Equal(["::1"], Single(state, Guid1).Saved.V6);
    }

    [Fact]
    public void WriteRead_RoundTripsThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ageo-dns-state-{Guid.NewGuid():N}.txt");
        try
        {
            DnsStateFile.Write(path, new Dictionary<string, SavedDns> { [Guid1] = new(["1.1.1.1"], []) }, ["127.0.0.1"]);

            var state = DnsStateFile.Read(path);
            Assert.Equal(["127.0.0.1"], state.RedirectTargets);
            Assert.Equal(["1.1.1.1"], Single(state, Guid1).Saved.V4);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_MissingFile_IsEmpty()
    {
        var state = DnsStateFile.Read(Path.Combine(Path.GetTempPath(), $"ageo-dns-none-{Guid.NewGuid():N}.txt"));

        Assert.Empty(state.Entries);
        Assert.Empty(state.RedirectTargets);
    }

    private static DnsStateEntry Single(DnsRedirectState state, string guid)
    {
        return Assert.Single(state.Entries, e => string.Equals(e.Guid, guid, StringComparison.OrdinalIgnoreCase));
    }
}
