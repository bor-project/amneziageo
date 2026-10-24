using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Текст, который передала другая программа (камера, браузер), открывает черновик конфигурации в настройках, когда
/// условия приняты и агент назвал свои конфигурации.
/// </summary>
public sealed class OutsideImportTests
{
    private const string ClientKey = "QW1uZXppYUdlbyBpbXBvcnQgY2xpZW50IGtleSAwMDEh";
    private const string ServerKey = "QW1uZXppYUdlbyBpbXBvcnQgc2VydmVyIGtleSAwMDEh";

    [Fact]
    public void AConfig_OpensTheDraft()
    {
        var window = Window(accepted: true);
        window.Config.Apply([]);

        window.ImportFromOutside(Conf());

        Assert.Equal("settings", window.Nav);
        Assert.Equal("config", window.SettingsSection);
        Assert.True(window.Config.IsCreatingSectionConfig);
        Assert.Contains("[Peer]", window.Config.SectionConfigText);
        Assert.Equal("example.net", window.Config.SectionConfigName);
        Assert.Equal(string.Empty, window.Config.SectionConfigStatus);
    }

    [Fact]
    public void TheDraft_WaitsForTheCatalogue()
    {
        var window = Window(accepted: true);

        window.ImportFromOutside(Conf());
        var early = window.Config.IsCreatingSectionConfig;
        window.Config.Apply([new ConfigEntry("example.net", "example.net:51820", false, ConnectionStatus.Disconnected, [])]);

        Assert.False(early);
        Assert.True(window.Config.IsCreatingSectionConfig);
        Assert.Equal("example.net (2)", window.Config.SectionConfigName);
    }

    [Fact]
    public void TheDraft_WaitsForTheTerms()
    {
        var window = Window(accepted: false);
        window.Config.Apply([]);

        window.ImportFromOutside(Conf());
        var early = window.Config.IsCreatingSectionConfig;
        window.ShowTermsGate = false;

        Assert.False(early);
        Assert.True(window.Config.IsCreatingSectionConfig);
        Assert.Contains("[Peer]", window.Config.SectionConfigText);
    }

    [Fact]
    public void AVpnLink_OpensTheDraftUnderItsName()
    {
        var window = Window(accepted: true);
        window.Config.Apply([]);

        window.ImportFromOutside(VpnLinkCodec.Encode(Conf(), "office"));

        Assert.Equal("office", window.Config.SectionConfigName);
        Assert.Contains("[Interface]", window.Config.SectionConfigText);
    }

    [Fact]
    public void AnAppLink_PutsTheSubscriptionAddressInTheDraft()
    {
        var window = Window(accepted: true);
        window.Config.Apply([]);

        window.ImportFromOutside("amneziageo://import?url=https%3A%2F%2Fexample.net%3A2096%2Fsub%2Fabcdef");

        Assert.True(window.Config.IsCreatingSectionConfig);
        Assert.Equal("https://example.net:2096/sub/abcdef", window.Config.SectionConfigText);
        Assert.Equal(string.Empty, window.Config.SectionConfigStatus);
    }

    [Fact]
    public void AnUnknownText_StaysInTheDraftUnderTheNotice()
    {
        var window = Window(accepted: true);
        window.Config.Apply([]);

        window.ImportFromOutside("just a note");

        Assert.True(window.Config.IsCreatingSectionConfig);
        Assert.Equal("just a note", window.Config.SectionConfigText);
        Assert.NotEqual(string.Empty, window.Config.SectionConfigStatus);
    }

    private static MainWindowViewModel Window(bool accepted)
    {
        return new MainWindowViewModel(new Silent(), new UiPreferences { AcceptedTerms = accepted ? LegalTexts.Version : 0 });
    }

    private static string Conf()
    {
        return string.Join('\n',
        [
            "[Interface]",
            $"PrivateKey = {ClientKey}",
            "Address = 10.0.1.2/32",
            string.Empty,
            "[Peer]",
            $"PublicKey = {ServerKey}",
            "AllowedIPs = 0.0.0.0/0",
            "Endpoint = example.net:51820",
        ]);
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
