using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Пока условия не приняты, окно показывает их и само на сервер обновлений не ходит.
/// </summary>
public sealed class TermsGateTests
{
    private static readonly StatusSnapshot _withUpdates =
        new("1.10.14.0", null, []) { UpdateUrl = "https://releases.test/latest/download/update.json" };

    [Fact]
    public void AWindowWithoutAcceptedTerms_ShowsThem()
    {
        var window = new MainWindowViewModel(new Agent(), new UiPreferences());

        Assert.True(window.ShowTermsGate);
        Assert.True(window.Gate.IsTerms);
        Assert.Equal(LegalTexts.Terms, window.Gate.Document);
    }

    [Fact]
    public void AWindowWithTheTermsAccepted_GoesStraightIn()
    {
        var window = new MainWindowViewModel(new Agent(), new UiPreferences { AcceptedTerms = LegalTexts.Version });

        Assert.False(window.ShowTermsGate);
    }

    [Fact]
    public void TermsOfAnOlderVersion_AreShownAgain()
    {
        var window = new MainWindowViewModel(new Agent(), new UiPreferences { AcceptedTerms = LegalTexts.Version - 1 });

        Assert.True(window.ShowTermsGate);
    }

    [Fact]
    public void BeforeTheTermsAreAccepted_TheWindowChecksNoUpdate()
    {
        var agent = new Agent();
        var general = new MainWindowViewModel(agent, new UiPreferences()).General;

        general.BeginAutoUpdateChecks();
        general.Apply(_withUpdates);

        Assert.DoesNotContain(agent.Sent, command => command.Op == IpcContract.OpCheckUpdate);
    }

    [Fact]
    public void OnceTheTermsAreAccepted_TheCheckPutOffGoesAtOnce()
    {
        var agent = new Agent();
        var prefs = new UiPreferences();
        var general = new MainWindowViewModel(agent, prefs).General;
        general.BeginAutoUpdateChecks();
        general.Apply(_withUpdates);

        prefs.AcceptedTerms = LegalTexts.Version;
        general.TermsAccepted();

        var check = Assert.Single(agent.Sent, command => command.Op == IpcContract.OpCheckUpdate);
        Assert.Equal(["silent", IpcContract.CheckUpdateDetached], check.Args);
    }

    [Fact]
    public void TheSwitchOfTheGate_TurnsToThePrivacy()
    {
        var window = new MainWindowViewModel(new Agent(), new UiPreferences());

        window.Gate.ShowCommand.Execute(LegalTexts.Privacy);

        Assert.True(window.Gate.IsPrivacy);
        Assert.False(window.Gate.IsTerms);
        Assert.NotEmpty(window.Gate.Body);
        Assert.True(window.Gate.HasVersion);
    }

    [Fact]
    public void TheThirdPartyPage_HasNoVersionAndNoAcceptance()
    {
        var page = new LegalViewModel(new UiPreferences { AcceptedTerms = LegalTexts.Version, AcceptedTermsOn = "2026-10-24" })
        {
            Document = LegalTexts.ThirdParty,
        };

        Assert.False(page.HasVersion);
        Assert.False(page.HasAcceptedLine);
        Assert.NotEmpty(page.Body);
    }

    [Fact]
    public void TheTermsPage_SaysWhenTheyWereAccepted()
    {
        var page = new LegalViewModel(new UiPreferences { AcceptedTerms = LegalTexts.Version, AcceptedTermsOn = "2026-10-24" });

        Assert.True(page.HasAcceptedLine);
        Assert.True(page.HasVersion);
    }

    // Записывает команды окна.
    private sealed class Agent : IAgentConnection
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

        public List<IpcCommand> Sent { get; } = [];

        public void Start()
        {
        }

        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            Sent.Add(command);
            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
