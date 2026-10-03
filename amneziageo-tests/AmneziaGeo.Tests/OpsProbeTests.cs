using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The probe sends every operation with no arguments, so it must hold back the ones an agent takes as a real
/// request even then: an empty assignment of a routing list turns the routing off.
/// </summary>
[Collection("Output")]
public sealed class OpsProbeTests : IDisposable
{
    private readonly BufferConsoleSink _console = new();

    /// <summary>
    /// ctor
    /// </summary>
    public OpsProbeTests()
    {
        Output.Sink = _console;
        Output.Json = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Output.Sink = new SystemConsoleSink();
        Output.Json = false;
    }

    [Fact]
    public async Task TheProbe_LeavesTheRoutingListAsItIs()
    {
        var link = new Link();

        await OpsCommands.RunAsync(link, "ops", ["--probe"]);

        Assert.NotEmpty(link.Sent);
        Assert.DoesNotContain(IpcContract.OpAssignRouting, link.Sent);
        Assert.DoesNotContain(IpcContract.OpSelectConfig, link.Sent);
        Assert.DoesNotContain(IpcContract.OpSetConnection, link.Sent);
    }

    // Remembers what was sent and refuses it, the way a handler refuses missing arguments.
    private sealed class Link : IAgentLink
    {
        public List<string> Sent { get; } = [];

        /// <inheritdoc/>
        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        /// <inheritdoc/>
        public StatusSnapshot Snapshot { get; } = new("1.0.0", null, []);

        /// <inheritdoc/>
        public Task<IpcAck> SendAsync(string op, params string[] args)
        {
            Sent.Add(op);
            return Task.FromResult(new IpcAck(false, "malformed command"));
        }
    }
}
