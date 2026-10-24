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

    [Fact]
    public void NothingUnsaved_OpensTheDraftWithoutAsking()
    {
        var window = Window(accepted: true);
        window.Config.Apply([]);

        window.ImportFromOutside(Conf());

        Assert.False(window.Sheet.IsOpen);
        Assert.True(window.Config.IsCreatingSectionConfig);
    }

    [Fact]
    public void AnEditedConfig_AsksFirstAndKeepsItsEdits()
    {
        var window = Edited();

        window.ImportFromOutside(Conf());

        Assert.True(window.Sheet.IsOpen);
        Assert.Contains("office", window.Sheet.Subtitle);
        Assert.Equal(2, window.Sheet.Options.Count);
        Assert.False(window.Config.IsCreatingSectionConfig);
        Assert.Equal("office", window.Config.OpenConfig);
        Assert.Equal("office-edit", window.Config.ConfigRename);
    }

    [Fact]
    public void OpeningTheIncoming_DropsTheEdits()
    {
        var window = Edited();

        window.ImportFromOutside(Conf());
        var asked = window.Sheet.IsOpen;
        Pick(window, 0);

        Assert.True(asked);
        Assert.False(window.Sheet.IsOpen);
        Assert.True(window.Config.IsCreatingSectionConfig);
        Assert.Equal("example.net", window.Config.SectionConfigName);
        window.Config.CancelSectionCommand.Execute(null);
        Assert.Equal("office", window.Config.OpenConfig);
        Assert.Equal("office", window.Config.ConfigRename);
    }

    [Fact]
    public void BackToTheEdits_KeepsThemAndDropsTheIncoming()
    {
        var window = Edited();

        window.ImportFromOutside(Conf());
        Pick(window, 1);

        Assert.False(window.Sheet.IsOpen);
        Assert.Equal("settings", window.Nav);
        Assert.Equal("config", window.SettingsSection);
        Assert.False(window.Config.IsCreatingSectionConfig);
        Assert.Equal("office", window.Config.OpenConfig);
        Assert.Equal("office-edit", window.Config.ConfigRename);
    }

    [Fact]
    public void AnOwnDraft_AsksFirstAndKeepsItsText()
    {
        var window = Window(accepted: true);
        window.Config.Apply([]);
        window.Config.BeginManualImportCommand.Execute(null);
        window.Config.SectionConfigText = "typed by hand";

        window.ImportFromOutside(Conf());

        Assert.True(window.Sheet.IsOpen);
        Assert.Equal("typed by hand", window.Config.SectionConfigText);
    }

    [Fact]
    public void ARoutingDraft_AsksFirst_AndOpeningDropsIt()
    {
        var window = Window(accepted: true);
        window.Config.Apply([]);
        window.Routing.BeginImportDraft();

        window.ImportFromOutside(Conf());
        var asked = window.Sheet.IsOpen;
        var kept = window.Routing.IsCreatingSectionRouting;
        Pick(window, 0);

        Assert.True(asked);
        Assert.True(kept);
        Assert.False(window.Routing.IsCreatingSectionRouting);
        Assert.True(window.Config.IsCreatingSectionConfig);
    }

    [Fact]
    public void AnEditedAccessPoint_AsksFirst_AndOpeningRevertsIt()
    {
        var window = Window(accepted: true);
        window.Config.Apply([]);
        window.Connections.HotspotSsid = "edited";

        window.ImportFromOutside(Conf());
        var asked = window.Sheet.IsOpen;
        Pick(window, 0);

        Assert.True(asked);
        Assert.False(window.Connections.HotspotDirty);
        Assert.True(window.Config.IsCreatingSectionConfig);
    }

    // Окно с открытой конфигурацией office и её несохранённым именем.
    private static MainWindowViewModel Edited()
    {
        var window = Window(accepted: true);
        window.Config.Apply([new ConfigEntry("office", "example.net:51820", false, ConnectionStatus.Disconnected, [])]);
        window.Config.OpenConfig = "office";
        window.Config.ConfigRename = "office-edit";
        return window;
    }

    // Нажимает строку шторки, если она есть.
    private static void Pick(MainWindowViewModel window, int index)
    {
        if (index < window.Sheet.Options.Count)
        {
            window.Sheet.PickCommand.Execute(window.Sheet.Options[index]);
        }
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
