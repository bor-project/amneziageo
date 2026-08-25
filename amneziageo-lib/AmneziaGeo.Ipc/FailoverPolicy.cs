namespace AmneziaGeo.Ipc;

/// <summary>
/// What one server looked like when the readings were taken.
/// </summary>
public sealed record FailoverReading(string Name, bool Up, LinkReading Link);

/// <summary>
/// Settings auto-switching is steered by.
/// </summary>
public sealed record FailoverSettings(bool Enabled, int ReturnMinutes);

/// <summary>
/// What auto-switching asks for.
/// </summary>
public enum FailoverAction
{
    /// <summary>
    /// Leave the route where it is.
    /// </summary>
    Stay,

    /// <summary>
    /// Carry the route off a server that stopped answering.
    /// </summary>
    Switch,

    /// <summary>
    /// Carry the route back to a server standing higher in the list.
    /// </summary>
    Return,
}

/// <summary>
/// The move auto-switching asks for and the server it names.
/// </summary>
public sealed record FailoverDecision(FailoverAction Action, string Config = "")
{
    /// <summary>
    /// Nothing to carry anywhere.
    /// </summary>
    public static readonly FailoverDecision Stay = new(FailoverAction.Stay);

    /// <summary>
    /// Off the server that fell, onto this one.
    /// </summary>
    public static FailoverDecision SwitchTo(string config)
    {
        return new FailoverDecision(FailoverAction.Switch, config);
    }

    /// <summary>
    /// Back onto this one.
    /// </summary>
    public static FailoverDecision ReturnTo(string config)
    {
        return new FailoverDecision(FailoverAction.Return, config);
    }
}

/// <summary>
/// Decides which server carries the default route. Reads nothing but the readings it is handed, so the whole
/// of auto-switching answers to tests without a stand.
/// </summary>
public sealed class FailoverPolicy
{
    /// <summary>
    /// Bad readings in a row before the route leaves a server.
    /// </summary>
    public const int FallSamples = 3;

    /// <summary>
    /// Healthy readings in a row before a server is trusted with the route again.
    /// </summary>
    public const int RiseSamples = 2;

    /// <summary>
    /// Rate below which the tunnel is carrying nothing anybody is waiting for.
    /// </summary>
    public const long SilentBitsPerSecond = 50_000;

    /// <summary>
    /// Seconds of that silence before the route may be moved without anyone noticing.
    /// </summary>
    public const int SilenceSeconds = 30;

    private readonly Dictionary<string, Streak> _streaks = new(StringComparer.Ordinal);

    /// <summary>
    /// Puts the picked server at the head of the priority list, keeping the rest in their order.
    /// </summary>
    public static IReadOnlyList<string> Raise(IEnumerable<string> order, string picked)
    {
        var names = order.ToList();
        var at = names.FindIndex(name => string.Equals(name, picked, StringComparison.Ordinal));
        if (at <= 0)
        {
            return names;
        }

        names.RemoveAt(at);
        names.Insert(0, picked);
        return names;
    }

    /// <summary>
    /// The servers auto-switching walks, in the order it walks them: the configuration order without the ones
    /// left out of it.
    /// </summary>
    public static IReadOnlyList<string> Participants(IEnumerable<string> order, string? skipped)
    {
        var left = NameList.Split(skipped).ToHashSet(StringComparer.Ordinal);
        return order.Where(name => !left.Contains(name)).ToList();
    }

    /// <summary>
    /// The server the route is handed to next: the one the decision names, unless it has already been dialled in
    /// this search and another participant has not. An empty name says every one of them has been.
    /// </summary>
    public static string Walk(IEnumerable<string> participants, string holder, string named, IReadOnlyCollection<string> dialled)
    {
        if (!dialled.Contains(named, StringComparer.Ordinal))
        {
            return named;
        }

        return participants.FirstOrDefault(name =>
            !string.Equals(name, holder, StringComparison.Ordinal) && !dialled.Contains(name, StringComparer.Ordinal))
            ?? string.Empty;
    }

    /// <summary>
    /// Folds one round of readings into what is known of each server and says where the route belongs. The
    /// readings are the participants in priority order, the holder is the server carrying the route now.
    /// </summary>
    public FailoverDecision Decide(IReadOnlyList<FailoverReading> readings, FailoverSettings settings, string holder, DateTimeOffset now)
    {
        if (!settings.Enabled)
        {
            _streaks.Clear();
            return FailoverDecision.Stay;
        }

        Fold(readings, holder, now);
        Forget(readings);

        var held = readings.FirstOrDefault(reading => string.Equals(reading.Name, holder, StringComparison.Ordinal));
        if (held is null)
        {
            return FailoverDecision.Stay;
        }

        // Every raised server going bad at once is the local link, not the servers; there is nowhere better to go.
        var raised = readings.Where(reading => reading.Up).ToList();
        if (raised.Count >= 2 && raised.TrueForAll(reading => Degraded(reading, holder)))
        {
            return FailoverDecision.Stay;
        }

        if (_streaks[held.Name].Bad >= FallSamples)
        {
            var next = readings.FirstOrDefault(reading => !Same(reading, held) && _streaks[reading.Name].Bad == 0);
            return next is null ? FailoverDecision.Stay : FailoverDecision.SwitchTo(next.Name);
        }

        return settings.ReturnMinutes > 0 ? Homeward(readings, held, settings.ReturnMinutes, now) : FailoverDecision.Stay;
    }

    // The route goes back only to a server standing higher in the list, only once it has answered for the whole
    // wait, and only while nothing is moving through the tunnel.
    private FailoverDecision Homeward(IReadOnlyList<FailoverReading> readings, FailoverReading held, int minutes, DateTimeOffset now)
    {
        if (_streaks[held.Name].SilentSince is not { } silent || now - silent < TimeSpan.FromSeconds(SilenceSeconds))
        {
            return FailoverDecision.Stay;
        }

        var wait = TimeSpan.FromMinutes(minutes);
        foreach (var reading in readings)
        {
            if (Same(reading, held))
            {
                break;
            }

            var streak = _streaks[reading.Name];
            if (streak.Good >= RiseSamples && streak.HealthySince is { } since && now - since >= wait)
            {
                return FailoverDecision.ReturnTo(reading.Name);
            }
        }

        return FailoverDecision.Stay;
    }

    // Folds one round into the streaks each server is judged by.
    private void Fold(IReadOnlyList<FailoverReading> readings, string holder, DateTimeOffset now)
    {
        foreach (var reading in readings)
        {
            if (!_streaks.TryGetValue(reading.Name, out var streak))
            {
                streak = new Streak();
                _streaks[reading.Name] = streak;
            }

            if (Degraded(reading, holder))
            {
                streak.Bad++;
                streak.Good = 0;
                streak.HealthySince = null;
            }
            else if (reading.Up)
            {
                streak.Good++;
                streak.Bad = 0;
                streak.HealthySince ??= now;
            }
            else
            {
                // A server nobody has dialled says nothing either way.
                streak.Bad = 0;
                streak.Good = 0;
                streak.HealthySince = null;
            }

            streak.SilentSince = reading.Link.RxBitsPerSecond + reading.Link.TxBitsPerSecond < SilentBitsPerSecond
                ? streak.SilentSince ?? now
                : null;
        }
    }

    // Servers that left the list take their streaks with them.
    private void Forget(IReadOnlyList<FailoverReading> readings)
    {
        var present = readings.Select(reading => reading.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in _streaks.Keys.Where(name => !present.Contains(name)).ToList())
        {
            _streaks.Remove(name);
        }
    }

    private static bool Same(FailoverReading one, FailoverReading other)
    {
        return string.Equals(one.Name, other.Name, StringComparison.Ordinal);
    }

    // A server the route has no business on: the tunnel under it is down, or it keeps re-establishing while
    // nothing arrives, or it drops and delays enough to be felt. A loss share nobody measured says nothing.
    private static bool Degraded(FailoverReading reading, string holder)
    {
        if (!reading.Up)
        {
            return string.Equals(reading.Name, holder, StringComparison.Ordinal);
        }

        var link = reading.Link;
        return (LinkHealth.Churning(link.HandshakesPerMinute) && link.RxBitsPerSecond == 0)
            || (LinkHealth.LossKnown(link.LossPercent) && LinkHealth.Lossy(link.LossPercent))
            || link.RttMs >= LinkHealth.SlowMs;
    }

    // What the last rounds said about one server.
    private sealed class Streak
    {
        public int Bad;
        public int Good;
        public DateTimeOffset? HealthySince;
        public DateTimeOffset? SilentSince;
    }
}
