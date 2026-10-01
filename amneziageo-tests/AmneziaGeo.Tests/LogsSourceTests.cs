using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The resolver log is a source of the viewer beside the agent log, read from the level the agent records at.
/// </summary>
public sealed class LogsSourceTests
{
    [Fact]
    public void TheResolverLog_StandsBesideTheAgentLog()
    {
        var logs = Logs(new Recorder());

        Assert.Equal(["ageo", "dns", "routes", LogsViewModel.LiveType, LogsViewModel.ConfigType], logs.LogTypes);
    }

    [Fact]
    public void TheResolverLog_CarriesTheLevelBesideIt()
    {
        var logs = Logs(new Recorder());

        logs.SelectedLogType = "dns";

        Assert.True(logs.HasSideChoice);
        Assert.True(logs.IsStoredLog);
        Assert.False(logs.IsRouteLog);
    }

    [Theory]
    [InlineData("ageo", "warning")]
    [InlineData("dns", "warning")]
    [InlineData("routes", "")]
    public void AStoredLog_IsReadFromTheLevelItCarries(string type, string level)
    {
        var agent = new Recorder();
        var logs = Logs(agent);
        logs.CaptureLevel = "warning";
        logs.SelectedLogType = LogsViewModel.ConfigType;

        logs.SelectedLogType = type;

        var read = agent.Sent.Last(command => command.Op == IpcContract.OpReadLog);
        Assert.Equal(type, read.Args[0]);
        Assert.Equal(level, read.Args[3]);
    }

    private static LogsViewModel Logs(Recorder agent)
    {
        var prefs = new UiPreferences();
        return new LogsViewModel(new MainWindowViewModel(agent, prefs), agent, prefs);
    }

    // Remembers every command and answers it with nothing.
    private sealed class Recorder : IAgentConnection
    {
        /// <summary>
        /// The commands the viewer sent.
        /// </summary>
        public List<IpcCommand> Sent { get; } = [];

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
            Sent.Add(command);
            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
