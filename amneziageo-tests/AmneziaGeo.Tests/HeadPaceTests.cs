using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A head whose window is off the screen looks at the tunnel process and tells the link readings rarely, and its
/// home screen does not watch the network.
/// </summary>
public sealed class HeadPaceTests
{
    [Fact]
    public void WithTheWindowOnTheScreen_TheHeadLooksEveryThreeSeconds()
    {
        Assert.Equal(3_000, HeadPace.WatchMs(true));
    }

    [Fact]
    public void WithoutTheWindow_TheHeadLooksTenTimesRarer()
    {
        Assert.Equal(30_000, HeadPace.WatchMs(false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5_000)]
    public void WithTheWindowOnTheScreen_EveryLinkReadingIsTold(long sinceTheLast)
    {
        Assert.True(HeadPace.TellsLink(true, 100_000 + sinceTheLast, 100_000));
    }

    [Theory]
    [InlineData(5_000, false)]
    [InlineData(59_999, false)]
    [InlineData(60_000, true)]
    public void WithoutTheWindow_ALinkReadingIsToldOnceInThePause(long sinceTheLast, bool told)
    {
        Assert.Equal(told, HeadPace.TellsLink(false, 100_000 + sinceTheLast, 100_000));
    }

    [Fact]
    public void AHeadThatToldNothingYet_TellsTheFirstReading()
    {
        Assert.True(HeadPace.TellsLink(false, 100_000, 0));
    }

    [Fact]
    public void TheHomeScreen_WatchesTheNetworkFromTheStart()
    {
        var window = new MainWindowViewModel(new Silent(), new UiPreferences());

        Assert.True(window.Home.WatchesNetwork);
    }

    [Fact]
    public void AWindowOffTheScreen_StopsWatchingTheNetwork()
    {
        var window = new MainWindowViewModel(new Silent(), new UiPreferences());

        window.Home.WatchNetwork(false);

        Assert.False(window.Home.WatchesNetwork);
    }

    [Fact]
    public void AWindowBackOnTheScreen_WatchesTheNetworkAgain()
    {
        var window = new MainWindowViewModel(new Silent(), new UiPreferences());
        window.Home.WatchNetwork(false);

        window.Home.WatchNetwork(true);
        window.Home.WatchNetwork(true);

        Assert.True(window.Home.WatchesNetwork);
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
