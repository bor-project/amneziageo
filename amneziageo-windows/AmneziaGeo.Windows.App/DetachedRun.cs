namespace AmneziaGeo.Windows.App;

/// <summary>
/// Runs a work off the path of the caller, one run at a time.
/// </summary>
internal sealed class DetachedRun
{
    private int _running;

    /// <summary>
    /// Whether a run goes on.
    /// </summary>
    public bool Running => Volatile.Read(ref _running) != 0;

    /// <summary>
    /// Starts the work unless a run goes on, and answers whether it started.
    /// </summary>
    public bool Start(Func<Task> work)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
        return true;
    }
}
