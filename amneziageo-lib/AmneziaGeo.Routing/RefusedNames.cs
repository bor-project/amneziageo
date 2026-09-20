using System.Collections.Concurrent;

namespace AmneziaGeo.Routing;

/// <summary>
/// Holds the names a block rule refused, each with the moment it was last asked for.
/// </summary>
public sealed class RefusedNames
{
    /// <summary>
    /// How many names are held at once.
    /// </summary>
    public const int MaxNames = 1024;

    private readonly ConcurrentDictionary<string, long> _names = new(StringComparer.Ordinal);

    private readonly Func<long> _now;

    private readonly int _max;

    /// <summary>
    /// ctor
    /// </summary>
    public RefusedNames(int max = MaxNames, Func<long>? now = null)
    {
        _max = max < 1 ? MaxNames : max;
        _now = now ?? (() => Environment.TickCount64);
    }

    /// <summary>
    /// How many names are held right now.
    /// </summary>
    public int Count => _names.Count;

    /// <summary>
    /// Takes a refusal of the name.
    /// </summary>
    public void Note(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var key = Key(name);
        if (key.Length == 0)
        {
            return;
        }

        if (_names.Count >= _max && !_names.ContainsKey(key))
        {
            Sweep();
        }

        _names[key] = _now();
    }

    /// <summary>
    /// The names refused no longer ago than the window, each with the seconds since its refusal; a window of zero
    /// keeps every name held.
    /// </summary>
    public IReadOnlyList<(string Name, int IdleSeconds)> Fresh(int ttlSeconds)
    {
        var now = _now();
        var window = ttlSeconds > 0 ? ttlSeconds * 1000L : 0;
        var fresh = new List<(string Name, int IdleSeconds)>();
        foreach (var pair in _names)
        {
            var idle = now - pair.Value;
            if (window > 0 && idle > window)
            {
                _names.TryRemove(pair.Key, out _);

                continue;
            }

            fresh.Add((pair.Key, (int)Math.Max(idle / 1000, 0)));
        }

        return fresh;
    }

    /// <summary>
    /// Drops every name held.
    /// </summary>
    public void Clear() => _names.Clear();

    // Drops the names refused longest ago until there is room again.
    private void Sweep()
    {
        var order = new List<KeyValuePair<string, long>>(_names);
        order.Sort((left, right) => left.Value.CompareTo(right.Value));
        var many = Math.Max(order.Count - _max + 1, 1);
        for (var i = 0; i < many && i < order.Count; i++)
        {
            _names.TryRemove(order[i].Key, out _);
        }
    }

    private static string Key(string name) => name.TrimEnd('.').ToLowerInvariant();
}
