using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class UpdateRecheckTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOfferOrDownloadedPackageAllowsAnotherCheck(bool downloaded)
    {
        var (general, _) = Window(downloaded);

        Assert.True(general.CanCheckUpdate);
        Assert.True(general.CheckUpdateCommand.CanExecute(null));
        Assert.Equal(Loc.Instance.Get("Main_RecheckUpdatesButton"), general.CheckUpdateText);
    }

    [Fact]
    public async Task RecheckingAnOfferRequestsFreshDataAndAdoptsTheNextVersion()
    {
        var (general, agent) = Window(false);
        agent.Reply = () =>
        {
            general.Apply(Offer("1.11.4.9"));
            return Task.FromResult(new IpcAck(true, IpcMessage.Key("Agent_UpdateAvailable", "1.11.4.9")));
        };

        await general.CheckUpdateCommand.ExecuteAsync(null).ConfigureAwait(false);
        await general.CheckUpdateCommand.ExecuteAsync(null).ConfigureAwait(false);

        Assert.Equal(2, agent.Checks);
        Assert.Equal("1.11.4.9", general.UpdateVersion);
        Assert.False(general.UpdateDownloading);
        Assert.False(general.UpdateInstalling);
        Assert.Equal(Loc.Instance.Get("MainVm_UpdateUnchanged"), general.UpdateStatus);
    }

    [Fact]
    public async Task ACheckBlocksAnotherRequestAndTransferUntilItsReply()
    {
        var (general, agent) = Window(true);
        var pending = new TaskCompletionSource<IpcAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.Reply = () => pending.Task;
        var running = general.CheckUpdateCommand.ExecuteAsync(null);

        Assert.True(general.UpdateChecking);
        Assert.False(general.CheckUpdateCommand.CanExecute(null));
        Assert.False(general.ApplyUpdateCommand.CanExecute(null));
        Assert.False(general.DownloadUpdateCommand.CanExecute(null));
        general.Apply(Offer("1.11.4.8", true));
        Assert.True(general.UpdateChecking);
        await general.CheckUpdateCommand.ExecuteAsync(null).ConfigureAwait(false);
        Assert.Equal(1, agent.Checks);

        pending.SetResult(new IpcAck(true, IpcMessage.Key("Agent_UpdateAvailable", "1.11.4.8")));
        await running.ConfigureAwait(false);

        Assert.False(general.UpdateChecking);
        Assert.True(general.CanCheckUpdate);
        Assert.True(general.UpdateDownloaded);
        Assert.True(general.ApplyUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task AFailedCheckPreservesTheOfferAndVerifiedPackageAndCanRetry()
    {
        var (general, agent) = Window(true);
        agent.Reply = () => Task.FromResult(new IpcAck(false, IpcMessage.Key("Agent_UpdateServerUnavailable")));

        await general.CheckUpdateCommand.ExecuteAsync(null).ConfigureAwait(false);

        Assert.Equal("1.11.4.8", general.UpdateVersion);
        Assert.True(general.UpdateAvailable);
        Assert.True(general.UpdateDownloaded);
        Assert.True(general.CanCheckUpdate);
        Assert.Equal(Loc.Instance.Get("Agent_UpdateServerUnavailable"), general.UpdateStatus);

        agent.Reply = () => Task.FromResult(new IpcAck(true, IpcMessage.Key("Agent_UpdateAvailable", "1.11.4.8")));
        await general.CheckUpdateCommand.ExecuteAsync(null).ConfigureAwait(false);

        Assert.Equal(2, agent.Checks);
        Assert.True(general.UpdateDownloaded);
        Assert.Equal(Loc.Instance.Get("MainVm_UpdateUnchanged"), general.UpdateStatus);
    }

    [Fact]
    public async Task ATransportFailureLeavesTheNextCheckAvailable()
    {
        var (general, agent) = Window(true);
        agent.Reply = () => Task.FromException<IpcAck>(new IOException("disconnected"));

        await general.CheckUpdateCommand.ExecuteAsync(null).ConfigureAwait(false);

        Assert.False(general.UpdateChecking);
        Assert.True(general.UpdateDownloaded);
        Assert.True(general.CanCheckUpdate);
        Assert.Equal(Loc.Instance.Get("Agent_UpdateCheckFailed"), general.UpdateStatus);
    }

    [Fact]
    public async Task ANewOfferDropsThePreviousInstallActionWithoutStartingADownload()
    {
        var (general, agent) = Window(true);
        agent.Reply = () =>
        {
            general.Apply(Offer("1.11.4.9"));
            return Task.FromResult(new IpcAck(true, IpcMessage.Key("Agent_UpdateAvailable", "1.11.4.9")));
        };

        await general.CheckUpdateCommand.ExecuteAsync(null).ConfigureAwait(false);

        Assert.False(general.UpdateDownloaded);
        Assert.False(general.ApplyUpdateCommand.CanExecute(null));
        Assert.True(general.DownloadUpdateCommand.CanExecute(null));
        Assert.Equal(0, agent.Transfers);
        Assert.Equal(Loc.Instance.Get("MainVm_UpdateFound"), general.UpdateStatus);
    }

    [Fact]
    public void ATrayCheckClearsTheCheckingStatusInTheWindow()
    {
        var (general, _) = Window(true);
        general.Apply(Offer("1.11.4.8", true) with { UpdateChecking = true });
        Assert.True(general.UpdateChecking);

        general.Apply(Offer("1.11.4.8", true));

        Assert.False(general.UpdateChecking);
        Assert.True(general.UpdateDownloaded);
        Assert.Equal(Loc.Instance.Get("MainVm_UpdateUnchanged"), general.UpdateStatus);
    }

    [Fact]
    public void AFailedTrayCheckLeavesAnErrorAndThePreviousInstallAction()
    {
        var (general, _) = Window(true);
        general.Apply(Offer("1.11.4.8", true) with { UpdateChecking = true });

        general.Apply(Offer("1.11.4.8", true) with { UpdateCheckFailed = true });

        Assert.True(general.UpdateCheckFailed);
        Assert.True(general.ApplyUpdateCommand.CanExecute(null));
        Assert.Equal(Loc.Instance.Get("Agent_UpdateCheckFailed"), general.UpdateStatus);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task TransferSnapshotsBlockChecking(bool downloading, bool installing)
    {
        var (general, agent) = Window(false);
        general.Apply(Offer("1.11.4.8") with { UpdateDownloading = downloading, UpdateInstalling = installing });

        Assert.False(general.CheckUpdateCommand.CanExecute(null));
        await general.CheckUpdateCommand.ExecuteAsync(null).ConfigureAwait(false);
        Assert.Equal(0, agent.Checks);
    }

    [Theory]
    [InlineData((int)UpdateDownloadPhase.Downloading)]
    [InlineData((int)UpdateDownloadPhase.Installing)]
    public void TheAgentRejectsManualAndBackgroundChecksDuringTransfers(int phase)
    {
        var state = new UpdateState { DownloadPhase = (UpdateDownloadPhase)phase };

        Assert.False(state.TryBeginCheck(false));
        Assert.False(state.TryBeginCheck(true));
        Assert.False(state.CheckRunning);
    }

    [Fact]
    public void TheAgentSerializesManualAndSilentChecksAndReleasesFailures()
    {
        var state = new UpdateState { DownloadPhase = UpdateDownloadPhase.Downloaded };

        Assert.True(state.TryBeginCheck(true));
        Assert.False(state.Checking);
        Assert.False(state.TryBeginCheck(false));
        state.EndCheck(true);
        Assert.True(state.TryBeginCheck(false));
        Assert.True(state.Checking);
        state.EndCheck(true);
        Assert.True(state.CheckFailed);
        Assert.False(state.Checking);
        Assert.False(state.CheckRunning);
        Assert.True(state.TryBeginCheck(false));
        Assert.False(state.CheckFailed);
        state.EndCheck(false);
    }

    private static (GeneralViewModel General, Agent Agent) Window(bool downloaded)
    {
        var agent = new Agent();
        var general = new MainWindowViewModel(agent, new UiPreferences
        {
            AcceptedTerms = LegalTexts.Version,
            ShownUpdateVersion = "1.11.4.8",
        }).General;
        general.Apply(Offer("1.11.4.8", downloaded));
        return (general, agent);
    }

    private static StatusSnapshot Offer(string version, bool downloaded = false) => new("1.11.4.0", null, [])
    {
        UpdateUrl = "https://updates.test/update.json",
        UpdateAvailable = true,
        UpdateVersion = version,
        UpdateSetupUrl = "https://updates.test/setup-" + version + ".exe",
        UpdateSetupPath = downloaded ? "/nowhere/setup.exe" : string.Empty,
        UpdateDownloaded = downloaded,
    };

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

        public int Checks { get; private set; }
        public int Transfers { get; private set; }
        public Func<Task<IpcAck>> Reply { get; set; } = () => Task.FromResult(new IpcAck(true, string.Empty));

        public void Start()
        {
        }

        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            if (command.Op == IpcContract.OpCheckUpdate)
            {
                Checks++;
                return Reply();
            }

            if (command.Op is IpcContract.OpDownloadUpdate or IpcContract.OpApplyUpdate or IpcContract.OpReportUpdateDownload)
            {
                Transfers++;
            }

            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
