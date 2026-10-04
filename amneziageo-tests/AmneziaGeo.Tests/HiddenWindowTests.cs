using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Avalonia.Threading;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A window that left the screen rests: the periodic timers Avalonia runs for itself stand, the journal is not
/// polled, and both come back with the window.
/// </summary>
public sealed class HiddenWindowTests
{
    [Fact]
    public void APeriodicTimerOfAvalonia_StandsWhileTheWindowIsHidden()
    {
        using var kept = DispatcherTimer.Run(() => true, TimeSpan.FromHours(1));

        var paused = HiddenTimers.Pause();
        var resumed = HiddenTimers.Resume();

        Assert.True(paused >= 1);
        Assert.Equal(paused, resumed);
    }

    [Fact]
    public void ATimerOfTheApplication_KeepsRunning()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        timer.Tick += OnTick;
        timer.Start();
        try
        {
            HiddenTimers.Pause();

            Assert.True(timer.IsEnabled);
        }
        finally
        {
            HiddenTimers.Resume();
            timer.Stop();
        }
    }

    [Fact]
    public void TheJournalOfAHiddenWindow_IsNotPolled()
    {
        var window = new MainWindowViewModel(new Silent(), new UiPreferences());
        window.Diagnostics.SetActive(true);

        window.Diagnostics.Logs.Rest(true);

        Assert.False(window.Diagnostics.Logs.Polls);
    }

    [Fact]
    public void TheJournalOpenedInAHiddenWindow_WaitsForTheWindow()
    {
        var window = new MainWindowViewModel(new Silent(), new UiPreferences());
        window.Diagnostics.Logs.Rest(true);

        window.Diagnostics.SetActive(true);

        Assert.False(window.Diagnostics.Logs.Polls);
    }

    [Fact]
    public void TheJournal_IsPolledAgainWhenTheWindowIsBack()
    {
        var window = new MainWindowViewModel(new Silent(), new UiPreferences());
        window.Diagnostics.SetActive(true);
        window.Diagnostics.Logs.Rest(true);

        window.Diagnostics.Logs.Rest(false);

        Assert.True(window.Diagnostics.Logs.Polls);
    }

    [Fact]
    public void AJournalThatIsNotOpen_StaysUnpolledWhenTheWindowIsBack()
    {
        var window = new MainWindowViewModel(new Silent(), new UiPreferences());
        window.Diagnostics.Logs.Rest(true);

        window.Diagnostics.Logs.Rest(false);

        Assert.False(window.Diagnostics.Logs.Polls);
    }

    private static void OnTick(object? sender, EventArgs e)
    {
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
