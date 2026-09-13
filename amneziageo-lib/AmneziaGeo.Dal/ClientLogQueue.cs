namespace AmneziaGeo.Dal;

/// <summary>
/// Rows of a client process on their way to the agent, with the per-user log database for the rows the agent does not take.
/// </summary>
public sealed class ClientLogQueue(string databasePath) : IDisposable
{
    // Rows held back while no link takes them.
    private const int MaxPending = 1000;

    // How often an exiting process looks whether the link took the rows.
    private const int FlushPollMs = 20;

    // The least time the per-user log database gets for its rows on exit.
    private const int MinFileFlushMs = 500;

    private readonly Lock _gate = new();
    private readonly Queue<ClientLogRow> _pending = new();
    private readonly Lazy<SqliteLogStore?> _store = new(() => OpenStore(databasePath));
    private Func<ClientLogRow, Task<bool>>? _relay;
    private int _draining;

    // Set when the link refused a row, cleared by the next attach.
    private volatile bool _stalled;

    /// <summary>
    /// Queues a row and hands it to the attached link.
    /// </summary>
    public void Append(ClientLogRow row)
    {
        Spill(Enqueue(row));
        Kick();
    }

    /// <summary>
    /// Hands the queued and later rows to the agent through a link.
    /// </summary>
    public void Attach(Func<ClientLogRow, Task<bool>> relay)
    {
        _stalled = false;
        Volatile.Write(ref _relay, relay);
        Kick();
    }

    /// <summary>
    /// Keeps the rows queued until the next attach.
    /// </summary>
    public void Detach()
    {
        Volatile.Write(ref _relay, null);
    }

    /// <summary>
    /// Waits for the link to take the queued rows and writes the rest to the per-user log database.
    /// </summary>
    public void Flush(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!_stalled && Volatile.Read(ref _relay) is not null && Count() > 0 && Environment.TickCount64 < deadline)
        {
            Kick();
            Thread.Sleep(FlushPollMs);
        }

        Spill(TakeAll());
        if (!_store.IsValueCreated || _store.Value is not { } store)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(Math.Max(MinFileFlushMs, (int)(deadline - Environment.TickCount64)));
            store.FlushAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Detach();
        if (_store.IsValueCreated)
        {
            _store.Value?.Dispose();
        }
    }

    // Starts handing rows over unless a handover runs, no link is attached or nothing waits.
    private void Kick()
    {
        if (Volatile.Read(ref _relay) is null || Count() == 0 || Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        var emptied = await SendAllAsync().ConfigureAwait(false);
        Volatile.Write(ref _draining, 0);
        if (emptied)
        {
            // Picks up a row queued while the handover was finishing.
            Kick();
        }
    }

    // Hands the queued rows over in order; false when the link refused one or went away.
    private async Task<bool> SendAllAsync()
    {
        try
        {
            while (Peek() is { } row)
            {
                if (Volatile.Read(ref _relay) is not { } relay || !await relay(row).ConfigureAwait(false))
                {
                    _stalled = true;
                    return false;
                }

                Settle(row);
            }

            return true;
        }
        catch (Exception)
        {
            // A link that throws is taken as refusing the row.
            _stalled = true;
            return false;
        }
    }

    // Queues the row and returns the oldest ones pushed past the cap.
    private List<ClientLogRow> Enqueue(ClientLogRow row)
    {
        var overflow = new List<ClientLogRow>();
        lock (_gate)
        {
            _pending.Enqueue(row);
            while (_pending.Count > MaxPending)
            {
                overflow.Add(_pending.Dequeue());
            }
        }

        return overflow;
    }

    private ClientLogRow? Peek()
    {
        lock (_gate)
        {
            return _pending.TryPeek(out var row) ? row : null;
        }
    }

    // Drops the row the link took unless it already left the queue.
    private void Settle(ClientLogRow row)
    {
        lock (_gate)
        {
            if (_pending.TryPeek(out var head) && ReferenceEquals(head, row))
            {
                _pending.Dequeue();
            }
        }
    }

    private int Count()
    {
        lock (_gate)
        {
            return _pending.Count;
        }
    }

    private List<ClientLogRow> TakeAll()
    {
        lock (_gate)
        {
            var rows = _pending.ToList();
            _pending.Clear();
            return rows;
        }
    }

    // Writes rows the agent did not take to the per-user log database.
    private void Spill(List<ClientLogRow> rows)
    {
        if (rows.Count == 0 || _store.Value is not { } store)
        {
            return;
        }

        foreach (var row in rows)
        {
            store.AppendAgent(row.UnixMs, row.LevelId, row.Source, row.Message);
        }
    }

    private static SqliteLogStore? OpenStore(string databasePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var store = new SqliteLogStore(databasePath);

            // Rows written before the schema is ready wait in the store's queue; its writer loop drains them.
            _ = InitializeAsync(store);
            return store;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task InitializeAsync(SqliteLogStore store)
    {
        try
        {
            await store.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Without a writer loop the store takes no more rows.
            store.Dispose();
        }
    }
}
