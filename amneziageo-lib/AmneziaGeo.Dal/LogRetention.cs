namespace AmneziaGeo.Dal;

/// <summary>
/// How many rows one pass dropped from the agent, resolver and routing logs.
/// </summary>
public readonly record struct LogPruned(int Agent, int Dns, int Routes)
{
    /// <summary>
    /// Rows dropped from the three logs together.
    /// </summary>
    public int Total => Agent + Dns + Routes;
}

/// <summary>
/// Keeps the log tables within their row caps.
/// </summary>
public static class LogRetention
{
    /// <summary>
    /// Rows kept in each of the agent, resolver and routing logs.
    /// </summary>
    public const int MaxRows = 50_000;

    /// <summary>
    /// Diagnostic runs and target probes kept.
    /// </summary>
    public const int RunsKept = 50;

    /// <summary>
    /// The pause between two passes of a running agent.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Drops the oldest rows of every log table past its cap.
    /// </summary>
    public static async Task<LogPruned> PruneAsync(SqliteLogStore store, int maxRows = MaxRows, CancellationToken ct = default)
    {
        var agent = await store.PruneAsync(SqliteLogStore.AgentTable, maxRows, ct).ConfigureAwait(false);
        var dns = await store.PruneAsync(SqliteLogStore.DnsTable, maxRows, ct).ConfigureAwait(false);
        var routes = await store.PruneAsync(SqliteLogStore.RoutesTable, maxRows, ct).ConfigureAwait(false);
        await store.PruneAsync(SqliteLogStore.ChecksTable, RunsKept, ct).ConfigureAwait(false);
        await store.PruneAsync(SqliteLogStore.ProbeTable, RunsKept, ct).ConfigureAwait(false);
        return new LogPruned(agent, dns, routes);
    }
}
