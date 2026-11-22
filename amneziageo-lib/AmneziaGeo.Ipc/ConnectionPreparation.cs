namespace AmneziaGeo.Ipc;

public sealed class ConnectionPreparation
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _current;

    public CancellationTokenSource Begin(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _current?.Cancel();
            var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _current = attempt;
            return attempt;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _current?.Cancel();
        }
    }

    public T Start<T>(CancellationToken cancellationToken, Func<T> start)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return start();
        }
    }

    public void End(CancellationTokenSource attempt)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, attempt))
            {
                _current = null;
            }

            attempt.Dispose();
        }
    }
}
