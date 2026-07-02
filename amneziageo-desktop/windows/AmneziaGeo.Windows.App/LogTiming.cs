using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Small timing helpers for the connect/refresh trace (#82). A step logs its entry at Trace and its duration
/// at Debug, so at "Обычный" the log stays quiet, at "Отладка" every step carries a millisecond cost (the
/// answer to "why is it slow"), and at "Трасса" each step is also announced as it begins ("every action").
/// </summary>
internal static class LogTiming
{
    /// <summary>
    /// Begins a timed step. Dispose the returned value (a `using`) to log its duration at Debug.
    /// </summary>
    public static TimedStep Step(this ILogger logger, string step)
    {
        logger.LogTrace("begin {Step}", step);
        return new TimedStep(logger, step);
    }

    /// <summary>
    /// Times a scope and logs "{step} in {ms} ms" at Debug on dispose. A struct so a step on the hot connect
    /// path allocates nothing.
    /// </summary>
    internal readonly struct TimedStep : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _step;
        private readonly long _start;

        public TimedStep(ILogger logger, string step)
        {
            _logger = logger;
            _step = step;
            _start = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            var ms = (long)Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            _logger.LogDebug("{Step} in {Elapsed} ms", _step, ms);
        }
    }
}
