using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Timing helpers for the connect/refresh trace.
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
    /// Times a scope and logs its duration at Debug on dispose.
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
