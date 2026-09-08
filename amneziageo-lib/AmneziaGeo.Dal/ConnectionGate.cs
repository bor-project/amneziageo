using Microsoft.Data.Sqlite;

namespace AmneziaGeo.Dal;

/// <summary>
/// The slots a store's database work runs in, and the suspend that takes every slot while the database file
/// is moved or replaced.
/// </summary>
internal sealed class ConnectionGate
{
    // Database operations allowed to run at once.
    private const int Slots = 64;

    private readonly SemaphoreSlim _slots = new(Slots, Slots);
    private readonly SemaphoreSlim _suspending = new(1, 1);

    /// <summary>
    /// Takes a work slot, waiting while the store is suspended.
    /// </summary>
    public Task EnterAsync(CancellationToken ct)
    {
        return _slots.WaitAsync(ct);
    }

    /// <summary>
    /// Puts a work slot back.
    /// </summary>
    public void Leave()
    {
        _slots.Release();
    }

    /// <summary>
    /// Holds new work off and waits for the work in flight.
    /// </summary>
    public async Task<Suspension> SuspendAsync(CancellationToken ct)
    {
        await _suspending.WaitAsync(ct).ConfigureAwait(false);
        var taken = 0;
        try
        {
            while (taken < Slots)
            {
                await _slots.WaitAsync(ct).ConfigureAwait(false);
                taken++;
            }
        }
        catch (Exception)
        {
            Resume(taken);
            throw;
        }

        return new Suspension(this);
    }

    /// <summary>
    /// Holds new work off and waits for the work in flight, blocking the calling thread.
    /// </summary>
    public Suspension Suspend()
    {
        _suspending.Wait();
        for (var taken = 0; taken < Slots; taken++)
        {
            _slots.Wait();
        }

        return new Suspension(this);
    }

    // Puts the taken slots back and lets the next suspend in.
    private void Resume(int taken)
    {
        if (taken > 0)
        {
            _slots.Release(taken);
        }

        _suspending.Release();
    }

    /// <summary>
    /// The suspended store, let go again when this is disposed.
    /// </summary>
    internal sealed class Suspension(ConnectionGate gate) : IDisposable, IAsyncDisposable
    {
        private int _released;

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Resume(Slots);
            }
        }

        /// <inheritdoc/>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// An open connection holding a work slot until it is disposed.
/// </summary>
internal sealed class ConnectionLease(ConnectionGate gate, SqliteConnection connection) : IAsyncDisposable
{
    /// <summary>
    /// The open connection.
    /// </summary>
    public SqliteConnection Connection => connection;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync().ConfigureAwait(false);
        gate.Leave();
    }
}
