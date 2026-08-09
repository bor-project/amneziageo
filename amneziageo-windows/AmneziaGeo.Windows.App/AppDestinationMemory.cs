using System.Net;
using System.Security.Cryptography;
using System.Text;
using AmneziaGeo.Decl;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Remembers the addresses a tunneled app reached without ever looking up a name, and installs their routes before
/// it asks again. Such an address is knowable only by watching the app reach it, and that first attempt is the one
/// that fails - so the discovery is kept, restored on the next tunnel, and the moment any one of them shows up the
/// rest are routed with it. Keyed by the app rules themselves, so editing them starts a new memory.
/// </summary>
internal sealed class AppDestinationMemory
{
    // Enough for every data centre of every app a user routes; the oldest leave first.
    private const int MaxAddresses = 256;
    private const int TickMs = 1_000;
    private const int PersistEveryTicks = 5;
    private const int MaxArmAttempts = 60;

    private readonly IStateStore _store;
    private readonly DomainTracker _tracker;
    private readonly string _tunnelName;
    private readonly string _key;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    // Insertion order, oldest first, so the cap drops what has not been seen for longest.
    private readonly List<string> _known = [];
    private readonly HashSet<string> _index = new(StringComparer.Ordinal);
    private long _listId;
    private bool _armed;
    private int _dirty;

    /// <summary>
    /// ctor
    /// </summary>
    public AppDestinationMemory(IStateStore store, DomainTracker tracker, string tunnelName, IReadOnlyList<string> appRules, ILogger logger)
    {
        _store = store;
        _tracker = tracker;
        _tunnelName = tunnelName;
        _key = Key(appRules);
        _logger = logger;
    }

    /// <summary>
    /// Records addresses an app rule claimed and routes everything remembered along with them.
    /// </summary>
    public void Note(IReadOnlyList<string> ips)
    {
        var fresh = false;
        lock (_lock)
        {
            foreach (var ip in ips)
            {
                if (ip.Contains(':') || !_index.Add(ip))
                {
                    continue;
                }

                _known.Add(ip);
                fresh = true;
                if (_known.Count > MaxAddresses)
                {
                    _index.Remove(_known[0]);
                    _known.RemoveAt(0);
                }
            }
        }

        if (!fresh)
        {
            return;
        }

        Interlocked.Exchange(ref _dirty, 1);
        // One address of a service teaches the others: the app is running now, and its remaining addresses are
        // reached in rotation - routing them here spares each of them the failed attempt that would find it.
        Arm();
    }

    /// <summary>
    /// Records one address an app rule claimed.
    /// </summary>
    public void Note(IPAddress address)
    {
        Note([address.ToString()]);
    }

    /// <summary>
    /// Restores the remembered addresses, routes them, and writes back what this session adds.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            _listId = await _store.GetActiveRoutingListIdAsync(_tunnelName).ConfigureAwait(false) ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "the routing list of {Tunnel} could not be read; remembered app addresses are stored untagged", _tunnelName);
        }

        await LoadAsync(ct).ConfigureAwait(false);

        var ticks = 0;
        var attempts = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Bounded: the adapter is ready within a second or two, and an address that refuses to route is
                // left to the ordinary path instead of being retried for the whole session.
                if (!_armed && ++attempts <= MaxArmAttempts)
                {
                    _armed = Arm();
                }

                if (++ticks % PersistEveryTicks == 0 && Interlocked.Exchange(ref _dirty, 0) == 1)
                {
                    await PersistAsync(ct).ConfigureAwait(false);
                }

                await Task.Delay(TickMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        if (Volatile.Read(ref _dirty) == 1)
        {
            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var stored = await _store.GetDomainResolutionAsync(_tunnelName, _key, ct).ConfigureAwait(false);
            if (stored is null || stored.Ips.Count == 0)
            {
                return;
            }

            lock (_lock)
            {
                foreach (var ip in stored.Ips)
                {
                    if (!ip.Contains(':') && _index.Add(ip))
                    {
                        _known.Add(ip);
                    }
                }
            }

            _logger.LogInformation("{Count} address(es) your tunneled apps reached before are routed again from the start, so their first request does not have to fail to be recognised", stored.Ips.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "the addresses remembered for tunneled apps could not be read");
        }
    }

    // Installs every remembered address; false while the adapter is not ready, so the caller keeps trying.
    private bool Arm()
    {
        string[] snapshot;
        lock (_lock)
        {
            if (_known.Count == 0)
            {
                return true;
            }

            snapshot = [.. _known];
        }

        try
        {
            return _tracker.UpdateAppIps(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "the remembered app addresses could not be routed this round");
            return false;
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        List<string> snapshot;
        lock (_lock)
        {
            snapshot = [.. _known];
        }

        try
        {
            await _store.SaveDomainResolutionAsync(_tunnelName, new DomainResolution(_key, snapshot), _listId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "the addresses learned for tunneled apps could not be saved");
        }
    }

    // A key no domain can collide with, tied to the app rules in force: change them and the old memory is ignored.
    private static string Key(IReadOnlyList<string> appRules)
    {
        var ordered = appRules.Select(rule => rule.Trim().ToLowerInvariant()).Where(rule => rule.Length > 0).ToList();
        ordered.Sort(StringComparer.Ordinal);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ordered)));
        return "app:" + Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }
}
