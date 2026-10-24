using System.Diagnostics;

using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using AmneziaGeo.Windows.App;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The update check the window starts by itself keeps its next command, a disconnect among them, from waiting on the
/// update server.
/// </summary>
public sealed class DetachedUpdateCheckTests
{
    private static readonly TimeSpan _budget = TimeSpan.FromSeconds(5);

    [Fact]
    public void TheCheckOfAnOpenedWindow_AsksTheAgentToAnswerAtOnce()
    {
        var agent = new Agent();
        var general = new MainWindowViewModel(agent, new UiPreferences { AcceptedTerms = LegalTexts.Version }).General;
        general.BeginAutoUpdateChecks();

        general.Apply(new StatusSnapshot("1.10.10.0", null, []) { UpdateUrl = "https://releases.test/latest/download/update.json" });

        var check = Assert.Single(agent.Sent, command => command.Op == IpcContract.OpCheckUpdate);
        Assert.Equal(["silent", IpcContract.CheckUpdateDetached], check.Args);
    }

    [Fact]
    public async Task AWorkThatHangs_LeavesTheCallerAtOnce()
    {
        var run = new DetachedRun();
        var hang = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();

        Assert.True(run.Start(() => hang.Task));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(run.Running);
        hang.SetResult();
        Assert.True(await EndsAsync(run));
    }

    [Fact]
    public async Task ARunUnderWay_StartsNoSecond()
    {
        var run = new DetachedRun();
        var hang = new TaskCompletionSource();
        var runs = 0;

        Task Work()
        {
            Interlocked.Increment(ref runs);
            return hang.Task;
        }

        Assert.True(run.Start(Work));
        Assert.False(run.Start(Work));
        hang.SetResult();
        Assert.True(await EndsAsync(run));
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public async Task AFailedRun_LeavesRoomForTheNext()
    {
        var run = new DetachedRun();

        Assert.True(run.Start(() => Task.FromException(new HttpRequestException("the update server closed the connection"))));
        Assert.True(await EndsAsync(run));

        var next = new TaskCompletionSource();
        Assert.True(run.Start(() =>
        {
            next.SetResult();
            return Task.CompletedTask;
        }));
        await next.Task.WaitAsync(_budget);
    }

    // Waits for the run to end, and answers whether it ended within the budget.
    private static async Task<bool> EndsAsync(DetachedRun run)
    {
        var clock = Stopwatch.StartNew();
        while (run.Running)
        {
            if (clock.Elapsed > _budget)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    // Records the commands of the window.
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
