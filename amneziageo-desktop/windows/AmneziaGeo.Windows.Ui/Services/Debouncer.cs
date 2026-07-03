using System;
using System.Threading;
using System.Threading.Tasks;

namespace AmneziaGeo.Windows.Ui.Services;

/// <summary>
/// A trailing-edge debounce for auto-save (#116). <see cref="Schedule"/> (re)starts the quiet timer, so the
/// action runs once the caller stops scheduling for <c>ms</c> milliseconds. <see cref="Flush"/> runs a pending
/// action at once - call it before an editor is torn down (a config / list switch) so a just-typed edit is not
/// lost; <see cref="Cancel"/> drops a pending action. The bound field is updated per keystroke, so the owning
/// view-model always holds the latest value and only the (network) persist is debounced - unlike Binding.Delay,
/// which keeps the value in the control and would strand it on teardown.
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

    /// <summary>(Re)start the quiet timer; a fresh call cancels the previous one, so the action fires once the
    /// caller pauses for the debounce window.</summary>
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

    /// <summary>Run a still-pending action immediately (e.g. before the owner is discarded) so the edit persists.</summary>
    public void Flush()
    {
        if (_pending)
        {
            _cts?.Cancel();
            _pending = false;
            _ = _action();
        }
    }

    /// <summary>Drop a pending action without running it.</summary>
    public void Cancel()
    {
        _cts?.Cancel();
        _pending = false;
    }
}
