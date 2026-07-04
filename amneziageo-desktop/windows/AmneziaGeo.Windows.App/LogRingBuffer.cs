namespace AmneziaGeo.Windows.App;

/// <summary>
/// Bounded thread-safe ring buffer of recent agent log lines.
/// </summary>
internal sealed class LogRingBuffer(int capacity = 300)
{
    private readonly Queue<string> _lines = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// Appends a line, dropping the oldest once the capacity is exceeded.
    /// </summary>
    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > capacity)
            {
                _lines.Dequeue();
            }
        }
    }

    /// <summary>
    /// Returns the buffered lines, oldest first.
    /// </summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return [.. _lines];
        }
    }
}
