using System.Text;
using System.Threading.Channels;

using Microsoft.Data.Sqlite;

namespace AmneziaGeo.Dal;

/// <summary>
/// One structured log row: growing id, event time (unix ms), optional level token (ageo only), optional
/// source (ageo only), message.
/// </summary>
public readonly record struct LogRow(long Id, long UnixMs, string? Level, string? Source, string Message);

/// <summary>
/// A window of log rows, newest first, plus whether older rows exist before it.
/// </summary>
public sealed record LogPage(IReadOnlyList<LogRow> Rows, bool HasOlder);

/// <summary>
/// SQLite-backed structured log store: the agent log (ageo, leveled), the routing log (routes, levelless) and
/// the diagnostic runs (checks, levelless), each with a growing bigint key. Levels are normalized into a small log_levels dictionary (severity is the
/// id, so a "&gt;= level" viewer filter is a cheap range) and joined back for display. Appends are queued and
/// flushed in batches by a single writer loop; reads/clear/prune/export open their own connections. WAL makes
/// it safe across the agent and per-tunnel processes on the shared local disk.
/// </summary>
public sealed class SqliteLogStore : IDisposable
{
    // PRAGMA auto_vacuum value for incremental mode.
    public const long AutoVacuumIncremental = 2;

    /// <summary>
    /// Table for the agent log (leveled Serilog events).
    /// </summary>
    public const string AgentTable = "ageo";

    /// <summary>
    /// Table for the routing log (levelless route/DNS events).
    /// </summary>
    public const string RoutesTable = "routes";

    /// <summary>
    /// Table for the diagnostic runs (levelless; one row carries a whole rendered check).
    /// </summary>
    public const string ChecksTable = "checks";

    // Enqueued append not yet written; id is assigned by the table AUTOINCREMENT on insert. LevelId applies to
    // the agent table only (0 for routes).
    private readonly record struct Pending(string Table, long UnixMs, int LevelId, string? Source, string Message, TaskCompletionSource? Flush = null);

    private readonly string _connectionString;
    private readonly Channel<Pending> _queue = Channel.CreateUnbounded<Pending>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _writerLoop;

    private readonly string _databasePath;

    // Set while the store cannot be written to, cleared when it can again.
    private volatile string? _failure;

    // Severity id of WRN in the log_levels dictionary seeded below.
    private const int WarningLevelId = 4;

    // Suffix of the note left next to the database while it cannot be opened at all.
    private const string FailureSuffix = ".failure.txt";

    /// <summary>
    /// ctor
    /// </summary>
    public SqliteLogStore(string databasePath)
    {
        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString();
    }

    /// <summary>
    /// Creates the tables (WAL), seeds the level dictionary, and starts the writer loop. Corruption
    /// self-heals, escalating: first quarantine only the -wal/-shm sidecars (a stale pair fails recovery of
    /// an intact file - its rows survive), then quarantine the database and recreate it empty - losing old
    /// log rows must never keep the agent from starting. The writer loop starts even when none of that
    /// worked: it opens the database again on its next batch, so a log that could not be opened once does
    /// not stay dead for the life of the process.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await OpenOrRecreateAsync(ct).ConfigureAwait(false);
        _writerLoop = Task.Run(() => WriteLoopAsync(_shutdown.Token));
    }

    /// <summary>
    /// Why the store is not writing, or null while it is.
    /// </summary>
    public string? LastFailure => _failure;

    /// <summary>
    /// Drops the pooled connections to the database file, so a caller can move it aside.
    /// </summary>
    public void ClearPool()
    {
        SqliteConnection.ClearAllPools();
    }

    // Opens the store, quarantining a corrupt file in two steps. Never throws: a failure is recorded and the
    // next batch tries again.
    private async Task<bool> OpenOrRecreateAsync(CancellationToken ct)
    {
        try
        {
            await InitializeCoreAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException)
        {
            SqliteConnection.ClearAllPools();
            CorruptQuarantine.MoveAsideSidecars(_databasePath);
        }

        try
        {
            await InitializeCoreAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException)
        {
            SqliteConnection.ClearAllPools();
            CorruptQuarantine.MoveAside(_databasePath);
        }

        try
        {
            await InitializeCoreAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
            return false;
        }
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var wal = connection.CreateCommand();
            await using (wal.ConfigureAwait(false))
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                await wal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await EnableIncrementalVacuumAsync(connection, ct).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS log_levels (
                        id   INTEGER PRIMARY KEY,
                        name TEXT NOT NULL UNIQUE
                    );

                    INSERT OR IGNORE INTO log_levels (id, name) VALUES
                        (1, 'VRB'), (2, 'DBG'), (3, 'INF'), (4, 'WRN'), (5, 'ERR'), (6, 'FTL');

                    CREATE TABLE IF NOT EXISTS ageo (
                        id       INTEGER PRIMARY KEY AUTOINCREMENT,
                        ts       INTEGER NOT NULL,
                        level_id INTEGER NOT NULL REFERENCES log_levels(id),
                        source   TEXT,
                        msg      TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS routes (
                        id  INTEGER PRIMARY KEY AUTOINCREMENT,
                        ts  INTEGER NOT NULL,
                        msg TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS checks (
                        id  INTEGER PRIMARY KEY AUTOINCREMENT,
                        ts  INTEGER NOT NULL,
                        msg TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Queues a leveled agent-log row. Never blocks and never throws (logging must not break the caller).
    /// </summary>
    public void AppendAgent(long unixMs, int levelId, string? source, string message)
    {
        _queue.Writer.TryWrite(new Pending(AgentTable, unixMs, levelId, source, message));
    }

    /// <summary>
    /// Queues a levelless routing-log row.
    /// </summary>
    public void AppendRoute(long unixMs, string message)
    {
        _queue.Writer.TryWrite(new Pending(RoutesTable, unixMs, 0, null, message));
    }

    /// <summary>
    /// Queues one diagnostic run; the whole rendered report is the row, and no capture floor applies to it - a
    /// check is run by hand and its answer is what support reads first.
    /// </summary>
    public void AppendCheck(long unixMs, string message)
    {
        _queue.Writer.TryWrite(new Pending(ChecksTable, unixMs, 0, null, message));
    }

    /// <summary>
    /// Reads a window of rows newest-first: the live tail when beforeId is null, otherwise rows with id below
    /// it (page older). minLevelId (ageo) hides rows less severe than it; search matches message or source.
    /// </summary>
    public async Task<LogPage> QueryAsync(string table, long? beforeId, int limit, int? minLevelId, string? search, CancellationToken ct = default)
    {
        var name = Validate(table);
        var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                var (from, columns) = Projection(name);
                var where = BuildFilter(command, name, beforeId, minLevelId, search);

                // One row past the limit tells us whether older content exists without a second count query.
                command.CommandText = $"SELECT {columns} FROM {from}{where} ORDER BY {IdColumn(name)} DESC LIMIT $limit;";
                command.Parameters.AddWithValue("$limit", limit + 1);

                var rows = new List<LogRow>(limit + 1);
                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        rows.Add(ReadRow(reader));
                    }
                }

                var hasOlder = rows.Count > limit;
                if (hasOlder)
                {
                    rows.RemoveAt(rows.Count - 1);
                }

                return new LogPage(rows, hasOlder);
            }
        }
    }

    /// <summary>
    /// Counts rows matching the given level/search filter (used for the search match summary).
    /// </summary>
    public async Task<int> CountAsync(string table, int? minLevelId, string? search, CancellationToken ct = default)
    {
        var name = Validate(table);
        var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                var (from, _) = Projection(name);
                var where = BuildFilter(command, name, beforeId: null, minLevelId, search);
                command.CommandText = $"SELECT COUNT(*) FROM {from}{where};";
                var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return result is long count ? (int)Math.Min(count, int.MaxValue) : 0;
            }
        }
    }

    /// <summary>
    /// Empties one table; the growing key keeps advancing (ids are never reused).
    /// </summary>
    public async Task ClearAsync(string table, CancellationToken ct = default)
    {
        var name = Validate(table);
        await FlushAsync(ct).ConfigureAwait(false);
        var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = $"DELETE FROM {name};";
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Drops the oldest rows past maxRows so a table stays bounded; returns how many were removed.
    /// </summary>
    public async Task<int> PruneAsync(string table, int maxRows, CancellationToken ct = default)
    {
        var name = Validate(table);
        if (maxRows <= 0)
        {
            return 0;
        }

        var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                // Keep the newest maxRows by id; delete everything at or below the cutoff.
                command.CommandText =
                    $"""
                    DELETE FROM {name} WHERE id <= (
                        SELECT id FROM {name} ORDER BY id DESC LIMIT 1 OFFSET $keep
                    );
                    """;
                command.Parameters.AddWithValue("$keep", maxRows);
                var removed = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (removed > 0)
                {
                    await ReleaseFreePagesAsync(connection, ct).ConfigureAwait(false);
                }

                return removed;
            }
        }
    }

    // Pages a delete frees are handed back to the file only in this mode, and the mode itself is stored in the
    // file header: a database created without it keeps growing to its high-water mark until it is rebuilt once.
    private static async Task EnableIncrementalVacuumAsync(SqliteConnection connection, CancellationToken ct)
    {
        var read = connection.CreateCommand();
        await using (read.ConfigureAwait(false))
        {
            read.CommandText = "PRAGMA auto_vacuum;";
            if (await read.ExecuteScalarAsync(ct).ConfigureAwait(false) is long mode && mode == AutoVacuumIncremental)
            {
                return;
            }
        }

        var rebuild = connection.CreateCommand();
        await using (rebuild.ConfigureAwait(false))
        {
            rebuild.CommandText = "PRAGMA auto_vacuum=incremental; VACUUM;";
            try
            {
                await rebuild.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // Another process holds the file: the rebuild is not worth failing startup over, and a caller
                // that treats a SqliteException here as corruption would quarantine an intact log.
            }
        }
    }

    // Hands the pages freed by a prune back to the file system.
    private static async Task ReleaseFreePagesAsync(SqliteConnection connection, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "PRAGMA incremental_vacuum;";
            try
            {
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // The rows are already gone; the space returns on the next prune.
            }
        }
    }

    /// <summary>
    /// Streams every row of a table oldest-first through format into a text file at path.
    /// </summary>
    public async Task ExportAsync(string table, string path, Func<LogRow, string> format, CancellationToken ct = default)
    {
        var writer = new StreamWriter(path, append: false);
        await using (writer.ConfigureAwait(false))
        {
            await WriteAllAsync(table, writer, format, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Renders every row of a table oldest-first through format into a single string.
    /// </summary>
    public async Task<string> RenderAsync(string table, Func<LogRow, string> format, CancellationToken ct = default)
    {
        var buffer = new StringBuilder();
        var writer = new StringWriter(buffer);
        await using (writer.ConfigureAwait(false))
        {
            await WriteAllAsync(table, writer, format, ct).ConfigureAwait(false);
        }

        return buffer.ToString();
    }

    // Streams a table oldest-first through format into writer.
    private async Task WriteAllAsync(string table, TextWriter writer, Func<LogRow, string> format, CancellationToken ct)
    {
        var name = Validate(table);
        await FlushAsync(ct).ConfigureAwait(false);

        var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                var (from, columns) = Projection(name);
                command.CommandText = $"SELECT {columns} FROM {from} ORDER BY {IdColumn(name)} ASC;";

                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        await writer.WriteLineAsync(format(ReadRow(reader))).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Waits until the currently-queued rows are written.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // An empty-table sentinel carrying the barrier: the writer completes it once its own sentinel is reached.
        _queue.Writer.TryWrite(new Pending(string.Empty, 0, 0, null, string.Empty, barrier));

        using (ct.Register(() => barrier.TrySetCanceled(ct)))
        {
            await barrier.Task.ConfigureAwait(false);
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        var reader = _queue.Reader;
        while (await WaitToReadAsync(reader, ct).ConfigureAwait(false))
        {
            var batch = new List<Pending>();
            var barriers = new List<TaskCompletionSource>();
            while (reader.TryRead(out var item))
            {
                if (item.Table.Length == 0)
                {
                    if (item.Flush is not null)
                    {
                        barriers.Add(item.Flush);
                    }

                    continue;
                }

                batch.Add(item);
            }

            if (batch.Count > 0)
            {
                // Already dequeued: write with None so shutdown cannot cancel mid-batch and drop it.
                await WriteBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }

            // Complete only the barriers read this drain: rows queued before each are now in the written batch.
            foreach (var barrier in barriers)
            {
                barrier.TrySetResult();
            }
        }

        // Drain remaining rows on shutdown so nothing queued is lost.
        var tail = new List<Pending>();
        var tailBarriers = new List<TaskCompletionSource>();
        while (reader.TryRead(out var item))
        {
            if (item.Table.Length > 0)
            {
                tail.Add(item);
            }
            else if (item.Flush is not null)
            {
                tailBarriers.Add(item.Flush);
            }
        }

        if (tail.Count > 0)
        {
            await WriteBatchAsync(tail, CancellationToken.None).ConfigureAwait(false);
        }

        foreach (var barrier in tailBarriers)
        {
            barrier.TrySetResult();
        }
    }

    private static async Task<bool> WaitToReadAsync(ChannelReader<Pending> reader, CancellationToken ct)
    {
        try
        {
            return await reader.WaitToReadAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task WriteBatchAsync(IReadOnlyList<Pending> batch, CancellationToken ct)
    {
        // The database is gone when another process quarantined it: a connection opened before that still
        // writes, into the file under its new name, and every row from here on would be lost with it.
        if (File.Exists(_databasePath) && await TryWriteBatchAsync(batch, ct).ConfigureAwait(false))
        {
            return;
        }

        _failure ??= "the log database was replaced or refused a write";
        if (!await OpenOrRecreateAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        if (await TryWriteBatchAsync(batch, ct).ConfigureAwait(false))
        {
            ReportRecovery();
        }
    }

    // Records that the store cannot be written to, where it is still readable once the log itself is not.
    private void RecordFailure(Exception ex)
    {
        _failure = ex.Message;
        try
        {
            File.WriteAllText(_databasePath + FailureSuffix, $"{DateTimeOffset.Now:O} {ex}");
        }
        catch (Exception)
        {
            // Nowhere to report to; the flag still marks the gap for a caller that asks.
        }
    }

    // Marks the gap in the log itself the moment writing comes back.
    private void ReportRecovery()
    {
        if (_failure is not { } reason)
        {
            return;
        }

        _failure = null;
        try
        {
            File.Delete(_databasePath + FailureSuffix);
        }
        catch (Exception)
        {
            // The note outliving the failure is harmless.
        }

        AppendAgent(
            DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            WarningLevelId,
            null,
            $"the log database was opened again after it stopped taking rows ({reason}); the events from that window are lost");
    }

    private async Task<bool> TryWriteBatchAsync(IReadOnlyList<Pending> batch, CancellationToken ct)
    {
        try
        {
            var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using (connection.ConfigureAwait(false))
            {
                var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var command = connection.CreateCommand();
                    await using (command.ConfigureAwait(false))
                    {
                        command.Transaction = transaction;

                        foreach (var row in batch)
                        {
                            command.Parameters.Clear();
                            command.Parameters.AddWithValue("$ts", row.UnixMs);
                            command.Parameters.AddWithValue("$msg", row.Message);
                            if (row.Table == AgentTable)
                            {
                                command.CommandText = "INSERT INTO ageo (ts, level_id, source, msg) VALUES ($ts, $level, $source, $msg);";
                                command.Parameters.AddWithValue("$level", row.LevelId);
                                command.Parameters.AddWithValue("$source", (object?)row.Source ?? DBNull.Value);
                            }
                            else if (row.Table == RoutesTable)
                            {
                                command.CommandText = "INSERT INTO routes (ts, msg) VALUES ($ts, $msg);";
                            }
                            else if (row.Table == ChecksTable)
                            {
                                command.CommandText = "INSERT INTO checks (ts, msg) VALUES ($ts, $msg);";
                            }
                            else
                            {
                                continue;
                            }

                            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        }
                    }

                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A log must not break the writer loop; the caller reopens the database and tries once more.
            return false;
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var busy = connection.CreateCommand();
        await using (busy.ConfigureAwait(false))
        {
            busy.CommandText = "PRAGMA busy_timeout=3000;";
            await busy.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return connection;
    }

    // The FROM clause and the fixed 5-column projection (id, ts, level, source, msg) for a table; the levelless
    // tables have no level/source, so those columns come back NULL and ReadRow yields nulls for them.
    private static (string From, string Columns) Projection(string table)
    {
        return table == AgentTable
            ? ("ageo a JOIN log_levels l ON a.level_id = l.id", "a.id, a.ts, l.name, a.source, a.msg")
            : (table, "id, ts, NULL, NULL, msg");
    }

    // Qualified id column for ORDER BY / cursor: aliased in the agent join, bare for routes.
    private static string IdColumn(string table)
    {
        return table == AgentTable ? "a.id" : "id";
    }

    // Appends a WHERE clause for the id cursor, min-severity (ageo), and search substring; binds parameters.
    private static string BuildFilter(SqliteCommand command, string table, long? beforeId, int? minLevelId, string? search)
    {
        var agent = table == AgentTable;
        var idCol = IdColumn(table);
        var msgCol = agent ? "a.msg" : "msg";

        var clauses = new List<string>();
        if (beforeId is { } cursor)
        {
            clauses.Add($"{idCol} < $before");
            command.Parameters.AddWithValue("$before", cursor);
        }

        if (agent && minLevelId is { } min)
        {
            clauses.Add("a.level_id >= $minlevel");
            command.Parameters.AddWithValue("$minlevel", min);
        }

        if (!string.IsNullOrEmpty(search))
        {
            var like = agent
                ? $"({msgCol} LIKE $q ESCAPE '\\' OR a.source LIKE $q ESCAPE '\\')"
                : $"{msgCol} LIKE $q ESCAPE '\\'";
            clauses.Add(like);
            command.Parameters.AddWithValue("$q", $"%{EscapeLike(search)}%");
        }

        return clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
    }

    private static string EscapeLike(string value)
    {
        return value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }

    private static LogRow ReadRow(SqliteDataReader reader)
    {
        return new LogRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4));
    }

    private static string Validate(string table)
    {
        return table is AgentTable or RoutesTable or ChecksTable
            ? table
            : throw new ArgumentException($"unknown log table: {table}", nameof(table));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        try
        {
            _writerLoop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
        }

        _shutdown.Dispose();
    }
}
