using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The settings header carries the app name in the wide layout only.
/// </summary>
public sealed class NarrowHeaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InANarrowWindow_TheHeaderCarriesNoName(bool sectionOpen)
    {
        var window = Window(400, sectionOpen);

        Assert.True(window.IsCompact);
        Assert.False(window.ShowHeaderName);
    }

    [Fact]
    public void InAWideWindow_TheHeaderCarriesTheName()
    {
        var window = Window(987, sectionOpen: false);

        Assert.False(window.IsCompact);
        Assert.True(window.ShowHeaderName);
    }

    [Fact]
    public void TheName_FollowsTheWidthAtOnce()
    {
        var window = Window(987, sectionOpen: false);
        var raised = new List<string?>();
        window.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        window.WindowWidth = 400;

        Assert.Contains(nameof(MainWindowViewModel.ShowHeaderName), raised);
        Assert.False(window.ShowHeaderName);
    }

    // The settings screen of a window of the given width, on the section list or inside a section.
    private static MainWindowViewModel Window(double width, bool sectionOpen)
    {
        var window = new MainWindowViewModel(new Silent(), new UiPreferences());
        window.WindowWidth = width;
        window.Nav = "settings";
        window.SettingsDetailOpen = sectionOpen;
        return window;
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
