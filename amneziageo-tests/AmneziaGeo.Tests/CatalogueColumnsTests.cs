using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The catalogue of configurations picks one column or two by the width of its pane, and builds no cards before
/// that width is known: cards built for the wrong number of columns are built a second time.
/// </summary>
public sealed class CatalogueColumnsTests
{
    [Fact]
    public void BeforeThePaneIsMeasured_NeitherLayoutIsShown()
    {
        var catalogue = Catalogue();

        Assert.True(catalogue.HasConfigs);
        Assert.False(catalogue.ShowCardStack);
        Assert.False(catalogue.ShowCardGrid);
    }

    [Fact]
    public void ANarrowPane_ShowsOneColumn()
    {
        var catalogue = Catalogue();

        catalogue.PaneWidth = UiLayout.CompactWidth - 1;

        Assert.True(catalogue.ShowCardStack);
        Assert.False(catalogue.ShowCardGrid);
    }

    [Fact]
    public void AWidePane_ShowsTwoColumns()
    {
        var catalogue = Catalogue();

        catalogue.PaneWidth = UiLayout.CompactWidth;

        Assert.False(catalogue.ShowCardStack);
        Assert.True(catalogue.ShowCardGrid);
    }

    [Fact]
    public void TheWidthOfThePane_IsToldToTheView()
    {
        var catalogue = Catalogue();
        var told = new List<string?>();
        catalogue.PropertyChanged += (_, e) => told.Add(e.PropertyName);

        catalogue.PaneWidth = 600;

        Assert.Contains(nameof(ConfigViewModel.ShowCardStack), told);
        Assert.Contains(nameof(ConfigViewModel.ShowCardGrid), told);
    }

    private static ConfigViewModel Catalogue()
    {
        var agent = new Silent();
        var catalogue = new ConfigViewModel(new MainWindowViewModel(agent, new UiPreferences()), agent);
        catalogue.Apply([new ConfigEntry("ours", "vpn.example:51820", false, ConnectionStatus.Disconnected, [])]);
        return catalogue;
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
