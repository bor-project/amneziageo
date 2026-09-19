using System.Text;

using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.App;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Where plain 53 is taken by another program, the system reaches the name proxy over HTTPS instead. What it
/// sends there is a DNS message in a URL or a body, and what it is told to send is a mode of one setting.
/// </summary>
public sealed class LocalDohTests
{
    [Fact]
    public void AQueryInTheUrl_ComesBackAsItWasSent()
    {
        byte[] query = [0x12, 0x34, 0x01, 0x00, 0x00, 0x01];
        var carried = Convert.ToBase64String(query).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(query, LocalDohWire.FromBase64Url(carried));
    }

    [Fact]
    public void AUrlCarryingNothing_IsNoQuery()
    {
        Assert.Null(LocalDohWire.FromBase64Url(null));
        Assert.Null(LocalDohWire.FromBase64Url(""));
        Assert.Null(LocalDohWire.FromBase64Url("   "));
    }

    [Fact]
    public void AUrlCarryingRubbish_IsNoQuery()
    {
        Assert.Null(LocalDohWire.FromBase64Url("not base64 at all!!"));
    }

    [Fact]
    public void AQueryPastTheLimit_IsNotServed()
    {
        var oversized = Convert.ToBase64String(Encoding.ASCII.GetBytes(new string('a', LocalDohWire.MaxQueryBytes + 1)));

        Assert.Null(LocalDohWire.FromBase64Url(oversized));
    }

    [Fact]
    public void AnUnknownMode_LeavesTheTakeoverToTheWatch()
    {
        Assert.Equal(LocalDohModes.Auto, LocalDohModes.Default);
        Assert.Equal(LocalDohModes.Auto, LocalDohModes.Of("whatever"));
        Assert.Equal(LocalDohModes.Auto, LocalDohModes.Of(null));
        Assert.Equal(LocalDohModes.Off, LocalDohModes.Of(" OFF "));
        Assert.Equal(LocalDohModes.On, LocalDohModes.Of("on"));
        Assert.False(LocalDohModes.IsKnown("doh"));
    }

    [Fact]
    public void ThePlaceAPickerShows_NamesTheValueTheAgentStores()
    {
        Assert.Equal([LocalDohModes.Auto, LocalDohModes.Off, LocalDohModes.On], LocalDohModes.All);
        Assert.Equal([DnsTransports.Auto, DnsTransports.Plain, DnsTransports.Doh], DnsTransports.All);
        Assert.Equal(LocalDohModes.Default, LocalDohModes.All[0]);
        Assert.Equal(DnsTransports.Default, DnsTransports.All[0]);
        Assert.All(LocalDohModes.All, mode => Assert.True(LocalDohModes.IsKnown(mode)));
        Assert.All(DnsTransports.All, transport => Assert.True(DnsTransports.IsKnown(transport)));
    }

    [Fact]
    public void APlatformTakingNoSuchChoice_LeavesThePickersOut()
    {
        var snapshot = new StatusSnapshot("1.0", null, []);

        Assert.Equal(string.Empty, snapshot.DnsTransport);
        Assert.Equal(string.Empty, snapshot.LocalDoh);
    }
}
