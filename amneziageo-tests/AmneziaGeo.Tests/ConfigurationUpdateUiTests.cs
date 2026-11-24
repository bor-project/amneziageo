using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class ConfigurationUpdateUiTests
{
    [Fact]
    public void DeferringTheOfferKeepsTheUpdateInConfigurationSettings()
    {
        var prefs = new UiPreferences(false);
        var link = new Link();
        var window = new MainWindowViewModel(link, prefs);
        var entries = Entries("second");
        window.Config.Apply(entries);
        window.OfferConfigurationUpdates(entries);
        Assert.True(window.Sheet.IsOpen);
        Assert.Equal(2, window.Sheet.Options.Count);
        window.Sheet.Options[1].Run();
        window.Sheet.Close();
        window.Config.OpenConfig = "phone";
        Assert.True(window.Config.HasOpenConfigurationUpdate);
        Assert.Contains("RekeyTimeout", window.Config.OpenConfigurationUpdateFields, StringComparison.Ordinal);
        Assert.Empty(link.Commands.Where(one => one.Op == IpcContract.OpApplyConfigUpdate));
        window.OfferConfigurationUpdates(entries);
        Assert.False(window.Sheet.IsOpen);
        var reopened = new MainWindowViewModel(new Link(), prefs);
        reopened.Config.Apply(entries);
        reopened.OfferConfigurationUpdates(entries);
        Assert.False(reopened.Sheet.IsOpen);
    }

    [Fact]
    public void ANewRevisionOffersTheUpdateAgain()
    {
        var window = new MainWindowViewModel(new Link(), new UiPreferences(false));
        window.OfferConfigurationUpdates(Entries("second"));
        window.Sheet.Close();
        window.OfferConfigurationUpdates(Entries("third"));
        Assert.True(window.Sheet.IsOpen);
    }

    [Fact]
    public async Task SettingsApplyOnlyTheSelectedRevision()
    {
        var link = new Link();
        var window = new MainWindowViewModel(link, new UiPreferences(false));
        var entries = Entries("second");
        window.Config.Apply(entries);
        await window.Config.ApplyConfigurationUpdateAsync("phone", "second");
        var command = Assert.Single(link.Commands.Where(one => one.Op == IpcContract.OpApplyConfigUpdate));
        Assert.Equal(["phone", "second"], command.Args);
        Assert.DoesNotContain(link.Commands, one => one.Op == IpcContract.OpRefreshSubscription);
    }

    [Fact]
    public void SkippingASubscriptionKeepsTheConfigurationOfferDeferred()
    {
        var prefs = new UiPreferences(false);
        var window = new MainWindowViewModel(new Link(), prefs);
        window.OfferConfigurationUpdates(Entries("second"));
        window.Sheet.Close();
        window.Home.SkipConfigUpdateCommand.Execute(null);
        window.OfferConfigurationUpdates(Entries("second"));
        Assert.False(window.Sheet.IsOpen);
        window.OfferConfigurationUpdates(Entries("third"));
        window.Sheet.Close();
        Assert.DoesNotContain("config:phone\tsecond", prefs.SkippedConfigUpdates, StringComparison.Ordinal);
        Assert.Contains("config:phone\tthird", prefs.SkippedConfigUpdates, StringComparison.Ordinal);
    }

    [Fact]
    public void AModalAlreadyOpenIsNotReplaced()
    {
        var window = new MainWindowViewModel(new Link(), new UiPreferences(false));
        window.Sheet.Show("existing", string.Empty, []);
        window.OfferConfigurationUpdates(Entries("second"));
        Assert.Equal("existing", window.Sheet.Title);
        window.Sheet.Close();
        window.OfferConfigurationUpdates(Entries("second"));
        Assert.True(window.Sheet.IsOpen);
        Assert.NotEqual("existing", window.Sheet.Title);
    }

    private static ConfigEntry[] Entries(string revision) =>
    [
        new("phone", "vpn.example:443", false, ConnectionStatus.Disconnected, [], Subscription: "ours",
            Update: new ConfigurationUpdate(revision, "first", ["[Interface].RekeyTimeout", "[Interface].I1"])),
    ];

    private sealed class Link : IAgentConnection
    {
        public List<IpcCommand> Commands { get; } = [];
        public event Action? Connected { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }
        public event Action<StatusSnapshot>? SnapshotReceived { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            Commands.Add(command);
            return Task.FromResult(new IpcAck(true, command.Op == IpcContract.OpListSubscriptions ? "[]" : string.Empty));
        }
        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);
    }
}
