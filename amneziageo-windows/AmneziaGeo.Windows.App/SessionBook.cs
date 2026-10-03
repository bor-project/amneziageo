using System.Collections.Concurrent;

using AmneziaGeo.Ipc;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Keeps what the session of every configuration went through, for the diagnostics archive.
/// </summary>
internal sealed class SessionBook
{
    private readonly ConcurrentDictionary<string, SessionMarks> _marks = new(StringComparer.Ordinal);

    /// <summary>
    /// The marks of the session of a configuration.
    /// </summary>
    public SessionMarks Of(string config)
    {
        return _marks.GetOrAdd(config, _ => new SessionMarks());
    }
}
