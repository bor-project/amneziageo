namespace AmneziaGeo.Ipc;

/// <summary>
/// Pairs the acks of the agent with the commands they answer. The agent answers every command of a connection once
/// and in the order it came, so the ack of a command whose waiter gave up still comes in its turn: it is dropped
/// instead of answering the command sent after it.
/// </summary>
internal sealed class AckTurns
{
    private readonly Lock _gate = new();
    private TaskCompletionSource<IpcAck>? _pending;
    private int _owed;

    /// <summary>
    /// Opens the turn of a command about to be sent.
    /// </summary>
    public TaskCompletionSource<IpcAck> Expect()
    {
        var turn = new TaskCompletionSource<IpcAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending = turn;
        }

        return turn;
    }

    /// <summary>
    /// Closes the turn of a command that never left: no ack is owed for it.
    /// </summary>
    public void Withdraw(TaskCompletionSource<IpcAck> turn)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pending, turn))
            {
                _pending = null;
            }
        }
    }

    /// <summary>
    /// Gives up on a command that was sent; true while its ack is still owed, false when the ack came first.
    /// </summary>
    public bool Abandon(TaskCompletionSource<IpcAck> turn)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, turn))
            {
                return false;
            }

            _pending = null;
            _owed++;
            return true;
        }
    }

    /// <summary>
    /// Hands an ack to the command whose turn it is.
    /// </summary>
    public void Deliver(IpcAck ack)
    {
        TaskCompletionSource<IpcAck>? pending;
        lock (_gate)
        {
            if (_owed > 0)
            {
                _owed--;
                return;
            }

            pending = _pending;
            _pending = null;
        }

        pending?.TrySetResult(ack);
    }

    /// <summary>
    /// Ends every turn with the connection: the waiting command gets the reason, and nothing more is owed.
    /// </summary>
    public void Fail(IpcAck ack)
    {
        TaskCompletionSource<IpcAck>? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
            _owed = 0;
        }

        pending?.TrySetResult(ack);
    }
}
