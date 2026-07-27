using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Marks DNS names queried by matched apps so the proxy resolves them through the tunnel and routes the answer
/// even with no geo rule. Reads the DNS-Client ETW provider, which reports the initiating process across the
/// Windows DNS Client service - a socket-source lookup on the proxy would only ever see svchost (Dnscache).
/// </summary>
internal sealed class AppDnsTracker : IDisposable
{
    // Microsoft-Windows-DNS-Client.
    private static readonly Guid DnsClientProvider = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");
    // Event 3006: a DNS query is issued, in the caller's process context.
    private const int DnsQueryIssuedId = 3006;
    // How long a name stays app-tunneled after its last query by a matched app; longer than any app retry gap.
    private const long NameTtlMs = 600_000;
    private const int MaxNames = 4096;
    // Per-pid match decision cache; MatchesPid snapshots the process tree, so a chatty resolver skips it.
    private const long PidCacheTtlMs = 1000;
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private readonly AppMatcher _matcher;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, long> _names = new(StringComparer.Ordinal);
    // ETW handler is single-threaded; no lock needed.
    private readonly Dictionary<uint, (long Expiry, bool Match)> _pidMatch = [];
    private TraceEventSession? _session;

    /// <summary>
    /// Raised the first time a name is marked app-tunneled, so the proxy can drop any pre-mark local answer.
    /// </summary>
    public event Action<string>? NameLearned;

    /// <summary>
    /// ctor
    /// </summary>
    public AppDnsTracker(AppMatcher matcher, ILogger logger)
    {
        _matcher = matcher;
        _logger = logger;
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
    /// Reads the DNS-Client provider until the session closes (cancellation).
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var sessionName = "AmneziaGeoDns";
        try
        {
            using (_session = new TraceEventSession(sessionName, TraceEventSessionOptions.Create))
            {
                ct.Register(Stop);
                _session.EnableProvider(DnsClientProvider, TraceEventLevel.Informational, ulong.MaxValue);
                _session.Source.Dynamic.All += Handle;
                _logger.LogInformation("AppDnsTracker: ETW session {Name} started", sessionName);
                await Task.Run(() => _session.Source.Process(), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AppDnsTracker: session ended");
        }
    }

    private void Handle(TraceEvent evt)
    {
        if ((int)evt.ID != DnsQueryIssuedId)
        {
            return;
        }

        var pid = (uint)evt.ProcessID;
        if (pid == 0 || pid == OwnProcessId || !MatchesPidCached(pid))
        {
            return;
        }

        if (evt.PayloadByName("QueryName") is string name && name.Length > 0)
        {
            Mark(name);
        }
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
            _logger.LogInformation("app dns: {Name} -> tunnel", key);
            if (RouteLog.Enabled)
            {
                RouteLog.Note($"app dns {key} -> tunnel");
            }

            NameLearned?.Invoke(key);
        }
        else
        {
            _names[key] = expiry;
        }
    }

    // Bounded eviction that preserves live marks: sweep expired entries, then drop the nearest-to-expire, so an
    // overflow never wipes still-active app-tunnel domains at once.
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
    public void Dispose() => _session?.Dispose();
}
