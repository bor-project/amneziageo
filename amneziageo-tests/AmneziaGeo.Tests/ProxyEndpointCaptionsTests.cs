using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The proxy tab names the network of every pair of addresses once there is more than one pair: the network the
/// device serves and the Wi-Fi it joined. An agent that tells no places leaves the rows as they were.
/// </summary>
public sealed class ProxyEndpointCaptionsTests
{
    [Fact]
    public void TwoPairs_AreCaptionedByTheNetworksTheyBelongTo()
    {
        var rows = Rows(["10.147.73.185", "192.168.1.91"], [ProxyPlaces.Served, ProxyPlaces.Wifi]);

        Assert.Equal(["SOCKS5", "HTTP", "SOCKS5", "HTTP"], rows.Select(row => row.Label));
        Assert.Equal(["10.147.73.185:10808", "10.147.73.185:10809", "192.168.1.91:10808", "192.168.1.91:10809"], rows.Select(row => row.Value));
        Assert.Equal(Loc.Instance.Get("General_ProxyPlaceServed"), rows[0].Caption);
        Assert.Equal(Loc.Instance.Get("General_ProxyPlaceWifi"), rows[2].Caption);
        Assert.True(rows[0].HasCaption);
        Assert.True(rows[2].HasCaption);
    }

    [Fact]
    public void TheSecondRowOfAPair_CarriesNoCaption()
    {
        var rows = Rows(["10.147.73.185", "192.168.1.91"], [ProxyPlaces.Served, ProxyPlaces.Wifi]);

        Assert.False(rows[1].HasCaption);
        Assert.False(rows[3].HasCaption);
    }

    [Fact]
    public void OnePair_IsNotCaptioned()
    {
        var rows = Rows(["192.168.1.91"], [ProxyPlaces.Wifi]);

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, row => row.HasCaption);
    }

    [Fact]
    public void AnAgentThatTellsNoPlaces_LeavesTheRowsWithoutCaptions()
    {
        var rows = Rows(["192.168.1.47", "10.0.0.5"], null);

        Assert.Equal(4, rows.Count);
        Assert.DoesNotContain(rows, row => row.HasCaption);
    }

    [Fact]
    public void ANetworkNothingIsToldOf_IsNotCaptioned()
    {
        var rows = Rows(["10.147.73.185", "192.168.1.60"], [ProxyPlaces.Served, ProxyPlaces.Unnamed]);

        Assert.True(rows[0].HasCaption);
        Assert.False(rows[2].HasCaption);
    }

    [Fact]
    public void FewerPlacesThanAddresses_CaptionWhatTheyName()
    {
        var rows = Rows(["10.147.73.185", "192.168.1.91"], [ProxyPlaces.Served]);

        Assert.True(rows[0].HasCaption);
        Assert.False(rows[2].HasCaption);
    }

    [Fact]
    public void WithNoAddressAtAll_TheLoopbackPairStandsWithoutACaption()
    {
        var rows = Rows([], []);

        Assert.Equal(["127.0.0.1:10808", "127.0.0.1:10809"], rows.Select(row => row.Value));
        Assert.DoesNotContain(rows, row => row.HasCaption);
    }

    [Fact]
    public void TheCaptions_AreWrittenInBothLanguages()
    {
        Assert.Equal("Раздача этого телефона", Loc.GetIn("ru", "General_ProxyPlaceServed"));
        Assert.Equal("Сеть Wi-Fi", Loc.GetIn("ru", "General_ProxyPlaceWifi"));
        Assert.Equal("This phone's hotspot", Loc.GetIn("en", "General_ProxyPlaceServed"));
        Assert.Equal("Wi-Fi network", Loc.GetIn("en", "General_ProxyPlaceWifi"));
    }

    // The rows of the tab after one snapshot with the proxy on.
    private static IReadOnlyList<ProxyEndpointRow> Rows(IReadOnlyList<string> addresses, IReadOnlyList<string>? places)
    {
        var connections = new ConnectionsViewModel(new Silent());
        connections.Apply(new StatusSnapshot("1.0", null, []) { ProxyEnabled = true, ProxyAddresses = addresses, ProxyPlaces = places });
        return [.. connections.ProxyEndpoints];
    }

    // Answers every command with nothing.
    private sealed class Silent : IAgentConnection
    {
        public event Action? Connected
        {
            add { }
            remove { }
        }

        public event Action? Disconnected
        {
            add { }
            remove { }
        }

        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public Task<IpcAck> SendCommandAsync(IpcCommand command) => Task.FromResult(new IpcAck(true, string.Empty));

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
