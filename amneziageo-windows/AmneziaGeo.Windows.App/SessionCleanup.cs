using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// Takes down what a session changed step by step, so that a step that fails leaves the others their turn.
/// </summary>
internal sealed class SessionCleanup(ILogger logger, string name)
{
    /// <summary>
    /// How many steps failed.
    /// </summary>
    public int Failed { get; private set; }

    /// <summary>
    /// Runs a step.
    /// </summary>
    public void Run(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Report(what, ex);
        }
    }

    /// <summary>
    /// Runs a step that waits.
    /// </summary>
    public async Task RunAsync(string what, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(what, ex);
        }
    }

    private void Report(string what, Exception ex)
    {
        Failed++;
        logger.LogError(ex, "{Name}: taking down {What} after the session failed ({Reason}); the other steps of the cleanup still run", name, what, ex.Message);
    }
}
