using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Takes down what a session changed step by step, so that a step that fails leaves the others their turn, and
/// writes how long each step took.
/// </summary>
internal sealed class SessionCleanup(ILogger logger, string name, Func<long>? clock = null)
{
    /// <summary>
    /// The time from which a step is written as a slow one, in milliseconds.
    /// </summary>
    public const int SlowMs = 1000;

    private readonly Func<long> _clock = clock ?? Ticks;
    private readonly long _began = (clock ?? Ticks)();
    private string _longest = string.Empty;
    private long _longestMs = -1;

    /// <summary>
    /// How many steps failed.
    /// </summary>
    public int Failed { get; private set; }

    /// <summary>
    /// Runs a step.
    /// </summary>
    public void Run(string what, Action step)
    {
        var began = _clock();
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Report(what, ex);
        }

        Time(what, began);
    }

    /// <summary>
    /// Runs a step that waits.
    /// </summary>
    public async Task RunAsync(string what, Func<Task> step)
    {
        var began = _clock();
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(what, ex);
        }

        Time(what, began);
    }

    /// <summary>
    /// Writes how long the cleanup took and which of its steps was the longest.
    /// </summary>
    public void Done()
    {
        var elapsed = _clock() - _began;
        if (_longestMs < 0)
        {
            logger.LogInformation("{Name}: the session is taken down in {Elapsed} ms", name, elapsed);
            return;
        }

        logger.LogInformation("{Name}: the session is taken down in {Elapsed} ms; the longest step, {What}, took {Longest} ms", name, elapsed, _longest, _longestMs);
    }

    private static long Ticks() => Environment.TickCount64;

    // Writes the time of a step and keeps the longest one.
    private void Time(string what, long began)
    {
        var elapsed = _clock() - began;
        if (elapsed > _longestMs)
        {
            _longestMs = elapsed;
            _longest = what;
        }

        logger.Log(elapsed >= SlowMs ? LogLevel.Warning : LogLevel.Debug, "{Name}: taking down {What} took {Elapsed} ms", name, what, elapsed);
    }

    private void Report(string what, Exception ex)
    {
        Failed++;
        logger.LogError(ex, "{Name}: taking down {What} after the session failed ({Reason}); the other steps of the cleanup still run", name, what, ex.Message);
    }
}
