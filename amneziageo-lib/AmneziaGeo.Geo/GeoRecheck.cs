namespace AmneziaGeo.Geo;

/// <summary>
/// The sources a scheduled check of the rule databases did not reach and when they are asked again.
/// </summary>
public sealed class GeoRecheck(TimeSpan pause, int tries)
{
    private readonly HashSet<string> _missed = new(StringComparer.Ordinal);
    private DateTimeOffset _at = DateTimeOffset.MaxValue;
    private int _left;

    /// <summary>
    /// Returns the sources to ask again at the moment given; empty while none is due.
    /// </summary>
    public IReadOnlyList<string> Due(DateTimeOffset now) => now >= _at ? [.. _missed] : [];

    /// <summary>
    /// Takes the sources a check did not reach: a whole check starts the tries over, a repeat spends one.
    /// </summary>
    public void Took(IEnumerable<string> missed, DateTimeOffset now, bool whole)
    {
        ArgumentNullException.ThrowIfNull(missed);

        _left = whole ? tries : _left - 1;
        _missed.Clear();
        if (_left > 0)
        {
            _missed.UnionWith(missed);
        }

        _at = _missed.Count > 0 ? now + pause : DateTimeOffset.MaxValue;
    }
}
