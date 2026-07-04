using System;
using System.Threading;
using System.Threading.Tasks;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// Trailing-edge debounce for auto-save.
/// </summary>
internal sealed class Debouncer
{
    private readonly int _ms;
    private readonly Func<Task> _action;
    private CancellationTokenSource? _cts;
    private bool _pending;

    public Debouncer(int ms, Func<Task> action)
    {
        _ms = ms;
        _action = action;
    }

    /// <summary>
    /// (Re)starts the quiet timer.
    /// </summary>
    public void Schedule()
    {
        _cts?.Cancel();
        _pending = true;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = RunAsync(cts.Token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_ms, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested)
        {
            _pending = false;
            await _action();
        }
    }

    /// <summary>
    /// Runs a pending action immediately.
    /// </summary>
    public void Flush()
    {
        if (_pending)
        {
            _cts?.Cancel();
            _pending = false;
            _ = _action();
        }
    }

    /// <summary>
    /// Drops a pending action.
    /// </summary>
    public void Cancel()
    {
        _cts?.Cancel();
        _pending = false;
    }
}
