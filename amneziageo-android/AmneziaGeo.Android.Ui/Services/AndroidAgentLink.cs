using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Android.Ui.Services;

/// <summary>
/// Console link to the agent that lives in this process.
/// </summary>
internal sealed class AndroidAgentLink : IAgentLink
{
    private readonly AndroidAgentConnection _agent;

    /// <summary>
    /// ctor
    /// </summary>
    public AndroidAgentLink(AndroidAgentConnection agent)
    {
        _agent = agent;
        _agent.SnapshotReceived += Accept;
    }

    /// <inheritdoc/>
    public event Action<StatusSnapshot>? SnapshotReceived;

    /// <inheritdoc/>
    public StatusSnapshot Snapshot => _agent.Latest ?? throw new InvalidOperationException("no snapshot received");

    /// <inheritdoc/>
    public Task<IpcAck> SendAsync(string op, params string[] args) =>
        _agent.SendCommandAsync(new IpcCommand(op, args));

    /// <summary>
    /// Stops forwarding snapshots; the agent itself outlives the command.
    /// </summary>
    public void Detach() => _agent.SnapshotReceived -= Accept;

    private void Accept(StatusSnapshot snapshot) => SnapshotReceived?.Invoke(snapshot);
}
