using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The name typed over the suggested one in the import draft is the name the configuration is saved under.
/// </summary>
public sealed class ImportDraftNameTests
{
    private const string Typed = "typed name";

    private const string Address = "https://example.net:9080/sub/path/id";

    private const string Conf = """
        [Interface]
        # remark name
        PrivateKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=
        Address = 10.0.0.2/32

        [Peer]
        PublicKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=
        Endpoint = vpn.example:51820
        AllowedIPs = 0.0.0.0/0
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFileRenamedBeforeSaving_IsSavedUnderTheTypedName(bool first)
    {
        var agent = new Recorder();
        var catalogue = Catalogue(agent, first);

        catalogue.BeginImportDraft();
        catalogue.ApplyImportText(Conf, "file name");
        catalogue.SectionConfigName = Typed;
        await catalogue.SaveSectionCommand.ExecuteAsync(null);

        Assert.Equal(Typed, agent.ImportedName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATextTypedAfterTheName_IsSavedUnderTheTypedName(bool first)
    {
        var agent = new Recorder();
        var catalogue = Catalogue(agent, first);

        catalogue.BeginManualImportCommand.Execute(null);
        catalogue.SectionConfigName = Typed;
        catalogue.SectionConfigText = Conf;
        await catalogue.SaveSectionCommand.ExecuteAsync(null);

        Assert.Equal(Typed, agent.ImportedName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANameTypedAfterTheText_IsSavedUnderTheTypedName(bool first)
    {
        var agent = new Recorder();
        var catalogue = Catalogue(agent, first);

        catalogue.BeginManualImportCommand.Execute(null);
        catalogue.SectionConfigText = Conf;
        catalogue.SectionConfigName = Typed;
        await catalogue.SaveSectionCommand.ExecuteAsync(null);

        Assert.Equal(Typed, agent.ImportedName);
    }

    [Fact]
    public async Task APastedSubscriptionRenamedBeforeSaving_HandsTheTypedNameToItsConfig()
    {
        var agent = new Recorder();
        var catalogue = Catalogue(agent, false);

        catalogue.BeginImportDraft();
        catalogue.ApplyImportText(Address);
        catalogue.SectionConfigName = Typed;
        await catalogue.SaveSectionCommand.ExecuteAsync(null);

        Assert.Equal([Address, Typed, Typed], agent.SubscriptionArgs);
    }

    [Fact]
    public async Task ASubscriptionTypedAfterTheName_HandsTheTypedNameToItsConfig()
    {
        var agent = new Recorder();
        var catalogue = Catalogue(agent, false);

        catalogue.BeginManualImportCommand.Execute(null);
        catalogue.SectionConfigName = Typed;
        catalogue.SectionConfigText = Address;
        await catalogue.SaveSectionCommand.ExecuteAsync(null);

        Assert.Equal([Address, Typed, Typed], agent.SubscriptionArgs);
    }

    [Fact]
    public async Task APastedSubscriptionUnderTheSuggestedName_LeavesTheConfigNamesToTheServer()
    {
        var agent = new Recorder();
        var catalogue = Catalogue(agent, false);

        catalogue.BeginImportDraft();
        catalogue.ApplyImportText(Address);
        await catalogue.SaveSectionCommand.ExecuteAsync(null);

        Assert.Equal([Address, "example.net"], agent.SubscriptionArgs);
    }

    private static ConfigViewModel Catalogue(Recorder agent, bool first)
    {
        var host = new MainWindowViewModel(agent, new UiPreferences());
        var catalogue = new ConfigViewModel(host, agent);
        catalogue.Apply(first ? [] : [new ConfigEntry("ours", "vpn.example:51820", false, ConnectionStatus.Disconnected, [])]);
        return catalogue;
    }

    // Answers every command at once and keeps what the import and the subscription were sent with.
    private sealed class Recorder : IAgentConnection
    {
        public string? ImportedName { get; private set; }

        public IReadOnlyList<string> SubscriptionArgs { get; private set; } = [];

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

        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            if (command.Op == IpcContract.OpImportConfig)
            {
                ImportedName = command.Args[0];
            }

            if (command.Op == IpcContract.OpAddSubscription)
            {
                SubscriptionArgs = [.. command.Args];
            }

            return Task.FromResult(command.Op == IpcContract.OpListSubscriptions
                ? new IpcAck(true, "[]")
                : new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
