using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The editor says what the list weighs, the threshold and how much of it is decided on the fly when the relay
/// carries a part of it, all as the agent counted them, instead of a figure of its own. A list that tunnels every
/// datagram is left to the warning of its screen.
/// </summary>
public sealed class RelayNoticeTests
{
    [Fact]
    public void TheAnswer_CarriesTheWeightAndTheThreshold()
    {
        var answer = RoutingListEditorViewModel.ParseBudget(
            """{"limit":0,"trims":0,"relay":1,"ranges":11000,"names":5,"weight":11040,"threshold":11000,"live":11000}""");

        Assert.Equal(1, answer.Relay);
        Assert.Equal(11_000, answer.Ranges);
        Assert.Equal(5, answer.Names);
        Assert.Equal(11_040, answer.Weight);
        Assert.Equal(11_000, answer.Threshold);
        Assert.Equal(11_000, answer.Live);
    }

    [Fact]
    public void AnAnswerWithoutThem_ReadsAsNone()
    {
        var answer = RoutingListEditorViewModel.ParseBudget("""{"limit":0,"trims":0,"relay":0}""");

        Assert.Equal(0, answer.Relay);
        Assert.Equal(0, answer.Weight);
        Assert.Equal(0, answer.Threshold);
        Assert.Equal(0, answer.Live);
    }

    [Fact]
    public void TheNotice_NamesTheWeightItsPartsTheThresholdAndWhatIsDecidedOnTheFly()
    {
        var editor = new RoutingListEditorViewModel(new Silent())
        {
            RelayWeight = 12_345,
            RelayRanges = 12_001,
            RelayNames = 43,
            RelayThreshold = 11_000,
            RelayLive = 9_876,
            RouteRelayed = true,
        };

        Assert.True(editor.ShowRelayNotice);
        Assert.Contains("12345", editor.RelayNotice);
        Assert.Contains("12001", editor.RelayNotice);
        Assert.Contains("43", editor.RelayNotice);
        Assert.Contains("11000", editor.RelayNotice);
        Assert.Contains("9876", editor.RelayNotice);
        Assert.DoesNotContain("10", editor.RelayNotice.Replace("11000", string.Empty).Replace("12001", string.Empty));
    }

    [Fact]
    public void AListThatTunnelsEveryDatagram_IsLeftToTheWarningOfItsScreen()
    {
        var editor = new RoutingListEditorViewModel(new Silent())
        {
            RelayKnown = true,
            AllUdpActive = true,
        };

        Assert.False(editor.ShowRelayNotice);
    }

    [Fact]
    public void AFullList_SaysNothingOfTheRelay()
    {
        var editor = new RoutingListEditorViewModel(new Silent())
        {
            RouteRelayed = true,
            GlobalProxyActive = true,
        };

        Assert.False(editor.ShowRelayNotice);
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
