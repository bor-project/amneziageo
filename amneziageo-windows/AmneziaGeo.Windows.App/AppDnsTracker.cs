using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Marks DNS names queried by matched apps so the proxy resolves them through the tunnel and routes the answer
/// even with no geo rule, or, for apps kept past the tunnel, routes the answer past it. Reads the DNS-Client ETW
/// provider, which reports the initiating process across the Windows DNS Client service - a socket-source lookup on
/// the proxy would only ever see svchost (Dnscache).
/// </summary>
internal sealed partial class AppDnsTracker : IDisposable
{
    // Microsoft-Windows-DNS-Client.
    private static readonly Guid DnsClientProvider = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");
    // Event 3006: a DNS query is issued, in the caller's process context.
    private const int DnsQueryIssuedId = 3006;
    // How long a name stays marked after its last query by a matched app; longer than any app retry gap.
    private const long NameTtlMs = 600_000;
    private const int MaxNames = 4096;
    // Per-pid match decision cache; MatchesPid snapshots the process tree, so a chatty resolver skips it.
    private const long PidCacheTtlMs = 1000;
    // The timer resolution asked for while an answer waits for the report of its asker, in ms.
    private const uint WaitResolutionMs = 1;
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private readonly AppMatcher _matcher;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, long> _names = new(StringComparer.Ordinal);
    // When each name was last reported asked, by any program, as a Stopwatch timestamp.
    private readonly ConcurrentDictionary<string, long> _asked = new(StringComparer.Ordinal);
    // Completed and replaced on every reported ask, so a waiting answer wakes as its asker is reported.
    private TaskCompletionSource _reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // ETW handler is single-threaded; no lock needed.
    private readonly Dictionary<uint, (long Expiry, bool Match)> _pidMatch = [];
    private TraceSession? _session;
    private ETWTraceEventSource? _source;
    // The monitoring session of this tunnel.
    private readonly string _sessionName;
    // Whether the matched apps are kept past the tunnel rather than carried through it.
    private readonly bool _past;

    /// <summary>
    /// Raised the first time a name is marked, so the proxy can drop any pre-mark local answer.
    /// </summary>
    public event Action<string>? NameLearned;

    /// <summary>
    /// Raised when an answer starts waiting for the report of the asker of a name.
    /// </summary>
    public event Action<string>? Waiting;

    /// <summary>
    /// ctor
    /// </summary>
    public AppDnsTracker(AppMatcher matcher, ILogger logger, string? sessionName = null, bool past = false)
    {
        _matcher = matcher;
        _logger = logger;
        _sessionName = sessionName ?? WatchSession.Names;
        _past = past;
    }

    /// <summary>
    /// Whether a name was recently queried by a matched app.
    /// </summary>
    public bool IsTunneled(string name)
    {
        var key = Normalize(name);
        if (_names.TryGetValue(key, out var expiry))
        {
            if (expiry > Environment.TickCount64)
            {
                return true;
            }

            _names.TryRemove(new KeyValuePair<string, long>(key, expiry));
        }

        return false;
    }

    /// <summary>
    /// Marks a name outside the ETW path, for an app whose own resolver never reaches DNS-Client.
    /// </summary>
    public void MarkName(string name) => Mark(name);

    /// <summary>
    /// Notes a name as asked now, outside the ETW path.
    /// </summary>
    public void NoteAsked(string name)
    {
        var key = Normalize(name);
        if (key.Length == 0)
        {
            return;
        }

        if (_asked.Count >= MaxNames && !_asked.ContainsKey(key))
        {
            _asked.Clear();
        }

        _asked[key] = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _reported, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>
    /// Whether some program was reported asking the name at or after <paramref name="since"/>, a Stopwatch timestamp.
    /// </summary>
    public bool AskedSince(string name, long since) => Asked(Normalize(name), since);

    /// <summary>
    /// Hands the asks the provider logged so far to the reader, so the asker of a name just received is reported at once.
    /// </summary>
    public void Flush() => _session?.Flush();

    /// <summary>
    /// Waits until some program is reported asking the name at or after <paramref name="since"/>, at most
    /// <paramref name="limitMs"/>; true when one was.
    /// </summary>
    public async Task<bool> WaitAskedAsync(string name, long since, int limitMs)
    {
        var key = Normalize(name);
        if (Asked(key, since))
        {
            return true;
        }

        Waiting?.Invoke(key);

        // The reader wakes with the system timer: finer ticks for the few ms of the wait.
        var raised = TimeBeginPeriod(WaitResolutionMs) == 0;
        try
        {
            var deadline = Stopwatch.GetTimestamp() + (limitMs * Stopwatch.Frequency / 1000);
            while (true)
            {
                // The pulse is taken before the check: an ask noted after the check completes this very pulse.
                var pulse = Volatile.Read(ref _reported).Task;
                if (Asked(key, since))
                {
                    return true;
                }

                var left = deadline - Stopwatch.GetTimestamp();
                if (left <= 0)
                {
                    return false;
                }

                // Rounded up; the timer may still end a tick short, so the deadline is read again.
                var ms = (int)((left * 1000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency);
                await Task.WhenAny(pulse, Task.Delay(ms)).ConfigureAwait(false);
            }
        }
        finally
        {
            if (raised)
            {
                TimeEndPeriod(WaitResolutionMs);
            }
        }
    }

    /// <summary>
    /// Reads the DNS-Client provider until the session closes (cancellation).
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var sessionName = _sessionName;
        try
        {
            _logger.LogDebug("starting the session {Session} of name lookups", sessionName);
            using (_session = TraceSession.Start(sessionName, DnsClientProvider, DnsQueryIssuedId))
            using (_source = new ETWTraceEventSource(sessionName, TraceEventSourceType.Session))
            {
                ct.Register(Stop);
                _source.Dynamic.All += Handle;
                if (!_session.Filtered)
                {
                    _logger.LogDebug("the provider of name lookups writes every event of its own into the session {Session}; the asks are picked out here", sessionName);
                }

                if (_past)
                {
                    _logger.LogInformation("watching which programs look up which names, so an app you keep off the tunnel reaches its names past it from the first try");
                }
                else
                {
                    _logger.LogInformation("watching which programs look up which names, so an app you route through the tunnel takes its domains with it");
                }

                await Task.Run(() => _source.Process(), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "no longer watching which programs look up which names; per-app rules now depend on outgoing connections alone");
        }
    }

    private void Handle(TraceEvent evt)
    {
        if ((int)evt.ID != DnsQueryIssuedId)
        {
            return;
        }

        var pid = (uint)evt.ProcessID;
        if (pid == OwnProcessId || evt.PayloadByName("QueryName") is not string name || name.Length == 0)
        {
            return;
        }

        Report(pid, name);
    }

    /// <summary>
    /// Takes the report that the process asked the name: a matched process marks the name before the ask is noted,
    /// so an answer that wakes on the note finds the mark in place.
    /// </summary>
    internal void Report(uint pid, string name)
    {
        if (pid != 0 && MatchesPidCached(pid))
        {
            Mark(name);
        }

        NoteAsked(name);
    }

    private void Mark(string name)
    {
        var key = Normalize(name);
        if (key.Length == 0)
        {
            return;
        }

        var expiry = Environment.TickCount64 + NameTtlMs;
        if (_names.Count >= MaxNames && !_names.ContainsKey(key))
        {
            EvictForRoom();
        }

        if (_names.TryAdd(key, expiry))
        {
            if (_past)
            {
                _logger.LogInformation("{Name} was looked up by an app you keep off the tunnel, so its addresses take a route past the tunnel", key);
            }
            else
            {
                _logger.LogInformation("{Name} was looked up by an app you route through the tunnel, so this name goes through the tunnel too", key);
            }

            if (RouteLog.Enabled)
            {
                RouteLog.Note(_past ? $"app dns {key} -> past" : $"app dns {key} -> tunnel");
            }

            NameLearned?.Invoke(key);
        }
        else
        {
            _names[key] = expiry;
        }
    }

    private bool Asked(string key, long since) => _asked.TryGetValue(key, out var at) && at >= since;

    // Bounded eviction that preserves live marks: sweep expired entries, then drop the nearest-to-expire, so an
    // overflow never wipes still-active marks at once.
    private void EvictForRoom()
    {
        var now = Environment.TickCount64;
        foreach (var kv in _names)
        {
            if (kv.Value <= now)
            {
                _names.TryRemove(kv);
            }
        }

        if (_names.Count < MaxNames)
        {
            return;
        }

        var oldestKey = default(string);
        var oldestExpiry = long.MaxValue;
        foreach (var kv in _names)
        {
            if (kv.Value < oldestExpiry)
            {
                oldestExpiry = kv.Value;
                oldestKey = kv.Key;
            }
        }

        if (oldestKey is not null)
        {
            _names.TryRemove(oldestKey, out _);
        }
    }

    // Cached per-pid app match; recomputes at most every PidCacheTtlMs so a chatty process skips the snapshot.
    private bool MatchesPidCached(uint pid)
    {
        var now = Environment.TickCount64;
        if (_pidMatch.TryGetValue(pid, out var entry) && entry.Expiry > now)
        {
            return entry.Match;
        }

        var match = _matcher.MatchesPid(pid);
        if (_pidMatch.Count >= 4096)
        {
            _pidMatch.Clear();
        }

        _pidMatch[pid] = (now + PidCacheTtlMs, match);
        return match;
    }

    private static string Normalize(string name) => name.TrimEnd('.').ToLowerInvariant();

    private void Stop()
    {
        try
        {
            _session?.Stop();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Деструктор
    /// </summary>
    public void Dispose()
    {
        _source?.Dispose();
        _session?.Dispose();
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);
}
