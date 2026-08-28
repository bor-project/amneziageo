namespace AmneziaGeo.Ipc;

/// <summary>
/// A subscription and what the panel last reported about it, as the agent sees it.
/// </summary>
public sealed record SubscriptionEntry(
    string Name,
    string Url,
    string Title,
    int IntervalHours,
    long Upload,
    long Download,
    long Total,
    // Unix seconds, zero when the panel names no limit.
    long ExpiresAt,
    long CheckedAt,
    string LastError,
    int Configs,
    int Gone);
